using System.Text;
using System.Text.RegularExpressions;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Application;

public record ProgressEventArgs(string Message);

/// <summary>
/// Structured progress for the chunked gap-analysis pipeline. Fired whenever the
/// pipeline starts a new LLM request, finishes one, or receives streamed tokens from
/// the active one — allowing the UI to show exactly how far the analysis has gotten.
/// </summary>
public sealed record AnalysisProgressEventArgs(
    int RunId,
    int CompletedRequests,
    int TotalRequests,
    int CurrentRequestTokens,
    int CurrentRequestMaxTokens,
    string CurrentStep);

public partial class CrawlOrchestrator(
    IRapidApiClient client,
    ILlmAnalyzer analyzer,
    ISearchRunRepository repository)
{
    // --- Chunked-analysis tuning ---
    // Split the scraped listings into batches of this size for incremental LLM
    // summarisation. Each batch is sent as a small prompt -> fast on a 7B model.
    private const int ListingsPerChunk = 25;
    // Max output tokens per chunk-summary request.
    private const int ChunkSummaryTokens = 350;

    public event EventHandler<ProgressEventArgs>? Progress;

    /// <summary>
    /// Raised during AI gap-analysis with fine-grained, machine-readable progress
    /// (requests done vs total, streaming token counts within the current request).
    /// Subscribed by the web UI's AnalysisProgressService so the Report page can show
    /// a real percentage instead of a spinner.
    /// </summary>
    public event EventHandler<AnalysisProgressEventArgs>? AnalysisProgress;

    /// <summary>Number of report sections emitted by <see cref="BuildSectionPrompts"/>.</summary>
    private const int SectionCount = 5;

    private static readonly Regex TokenCountRegex =
        new(@"^Generating\.\.\.\s+(\d+) tokens", RegexOptions.Compiled);

    private void Report(string message) => Progress?.Invoke(this, new ProgressEventArgs(message));

    public async Task<SearchRun> RunAsync(string keyword, bool analyzeWithLlm, CancellationToken ct = default, int maxListings = int.MaxValue)
    {
        var run = new SearchRun { Keyword = keyword, StartedUtc = DateTime.UtcNow, Status = "Running" };
        run.Id = await repository.CreateRunAsync(run);
        Report($"[{run.Id}] Starting search for '{keyword}'.");

        try
        {
            await foreach (var listing in client.SearchListingsAsync(keyword, run.Id, ct))
            {
                if (run.ListingsFound >= maxListings) break;

                listing.SearchRunId = run.Id;
                await repository.AddListingAsync(listing);
                run.ListingsFound++;
                Report($"Found listing: {listing.Name} ({listing.Provider}/{listing.ApiSlug})");

                ct.ThrowIfCancellationRequested();

                var pages = await client.CaptureListingAsync(listing, ct);
                foreach (var page in pages)
                {
                    page.ListingId = listing.Id;
                    await repository.AddPageAsync(page);
                }

                run.PagesCrawled += pages.Count;
                Report($"Captured {pages.Count} discussions page(s) for '{listing.Name}'.");
            }

            run.CompletedUtc = DateTime.UtcNow;
            run.Status = "Completed";
            await repository.UpdateRunAsync(run);
            Report($"Crawl completed: {run.ListingsFound} listings, {run.PagesCrawled} pages captured.");
        }
        catch (OperationCanceledException)
        {
            run.Status = "Cancelled";
            run.CompletedUtc = DateTime.UtcNow;
            await repository.UpdateRunAsync(run);
            throw;
        }

        if (analyzeWithLlm && run.ListingsFound > 0)
        {
            Report("Starting chunked gap-analysis (local LLM)...");
            var listings = await repository.GetListingsAsync(run.Id);
            var reportText = await GenerateChunkedReportAsync(run.Id, keyword, listings, ct);
            await repository.AddReportAsync(new AnalysisReport
            {
                SearchRunId = run.Id,
                Model = "chunked-local-llm",
                ReportText = reportText
            });
            Report("Analysis report saved.");
        }

        return run;
    }

    public async Task<SearchRun> RunPopularAsync(bool analyzeWithLlm, CancellationToken ct = default, int maxListings = int.MaxValue)
    {
        var run = new SearchRun { Keyword = "popular-apis", StartedUtc = DateTime.UtcNow, Status = "Running" };
        run.Id = await repository.CreateRunAsync(run);
        Report($"[{run.Id}] Starting scrape of the RapidAPI 'Popular APIs' collection.");

        try
        {
            await foreach (var listing in client.PopularListingsAsync(run.Id, ct))
            {
                if (run.ListingsFound >= maxListings) break;

                listing.SearchRunId = run.Id;
                await repository.AddListingAsync(listing);
                run.ListingsFound++;
                Report($"Found listing: {listing.Name} ({listing.Provider}/{listing.ApiSlug})");

                ct.ThrowIfCancellationRequested();

                var pages = await client.CaptureListingAsync(listing, ct);
                foreach (var page in pages)
                {
                    page.ListingId = listing.Id;
                    await repository.AddPageAsync(page);
                }

                run.PagesCrawled += pages.Count;
                Report($"Captured {pages.Count} page(s) for '{listing.Name}'.");
            }

            run.CompletedUtc = DateTime.UtcNow;
            run.Status = "Completed";
            await repository.UpdateRunAsync(run);
            Report($"Popular APIs scrape completed: {run.ListingsFound} listings, {run.PagesCrawled} pages captured.");
        }
        catch (OperationCanceledException)
        {
            run.Status = "Cancelled";
            run.CompletedUtc = DateTime.UtcNow;
            await repository.UpdateRunAsync(run);
            throw;
        }

        return run;
    }

    public async Task<string> AnalyzeExistingRunAsync(int runId, CancellationToken ct = default)
    {
        var listings = await repository.GetListingsAsync(runId);
        if (listings.Count == 0)
            throw new InvalidOperationException(
                $"Run #{runId} has no listings to analyze — run a crawl first.");

        var run = (await repository.GetRunsAsync()).FirstOrDefault(r => r.Id == runId)
                ?? throw new InvalidOperationException($"Run #{runId} not found.");

        Report($"Generating chunked gap-analysis report for run #{runId} '{run.Keyword}' ({listings.Count} listings)...");

        var reportText = await GenerateChunkedReportAsync(runId, run.Keyword, listings, ct);

        await repository.AddReportAsync(new AnalysisReport
        {
            SearchRunId = runId,
            Model = "chunked-local-llm",
            ReportText = reportText
        });

        Report("Analysis report saved.");
        return reportText;
    }

    /// <summary>
    /// Builds a gap-analysis report by breaking the workload into multiple smaller
    /// chained LLM requests: first summarising listing chunks, then generating each
    /// report section separately. This is significantly faster than a single
    /// monolithic request on a 7B local model because each individual inference has
    /// a much smaller context window and output budget.
    /// </summary>
    /// <summary>
    /// Strips HTML tags/entities from a captured discussions page down to plain text.
    /// Deliberately dependency-free (regex-based) so the Application layer needs no parser.
    /// </summary>
    [GeneratedRegex(@"<script[\s\S]*?</script>|<style[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static string ExtractCommentText(string html)
    {
        var text = ScriptStyleRegex().Replace(html, " ");
        text = TagRegex().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private async Task<string> GenerateChunkedReportAsync(
        int runId,
        string keyword,
        List<ApiListing> listings,
        CancellationToken ct)
    {
        Report($"Building gap-analysis report for '{keyword}' ({listings.Count} listings)...");

        // ---- Load ALL captured pages and pair them with their API listing ----
        // Every CrawledPage carries ListingId (set at capture time), so each API's
        // overview page and its discussion pages are joined by that key — never scrambled.
        var overviewTextByListing = new Dictionary<int, string>();
        var commentsTextByListing = new Dictionary<int, string>();
        try
        {
            var pages = await repository.GetPagesForRunAsync(runId);
            foreach (var group in pages.GroupBy(p => p.ListingId))
            {
                var overview = string.Join(" ", group
                    .Where(p => p.PageType == "ApiOverview")
                    .OrderBy(p => p.Id)
                    .Select(p => ExtractCommentText(p.Html))
                    .Where(t => t.Length > 0));
                if (overview.Length > 0)
                    overviewTextByListing[group.Key] =
                        overview.Length > 800 ? overview[..800] + "…" : overview;

                var comments = string.Join(" | ", group
                    .Where(p => p.PageType == "Discussions")
                    .OrderBy(p => p.Id)   // preserve the original comment order
                    .Select(p => ExtractCommentText(p.Html))
                    .Where(t => t.Length > 0));
                if (comments.Length > 0)
                    commentsTextByListing[group.Key] =
                        comments.Length > 2000 ? comments[..2000] + "…" : comments;
            }
            Report($"Rich data loaded: overviews for {overviewTextByListing.Count}, " +
                   $"customer discussions for {commentsTextByListing.Count} of {listings.Count} listings.");
        }
        catch (Exception ex)
        {
            Report($"WARNING: could not load crawled pages ({ex.Message}) — analysing listing names only.");
        }

        // ---- Build one rich, self-contained profile block per API ----
        // The block bundles identity + overview + that API's OWN customer comments, so the
        // model always sees each review together with the exact API it belongs to.
        var listingBlocks = listings.Select(l =>
        {
            var sb = new StringBuilder();
            sb.Append($"### API: {l.Name} (provider: {l.Provider}, slug: {l.ApiSlug})");
            if (overviewTextByListing.TryGetValue(l.Id, out var ov))
                sb.Append($"\nOverview: {ov}");
            if (commentsTextByListing.TryGetValue(l.Id, out var cm))
                sb.Append($"\nCustomer reviews & discussion: {cm}");
            return sb.ToString();
        }).ToList();

        // ---- Adaptive chunking by character budget ----
        // Rich text varies wildly per listing; fixed batch sizes could overflow the model's
        // context. Pack blocks into chunks up to MaxChunkChars each (one block never split).
        const int MaxChunkChars = 12_000;
        var chunks = new List<List<string>>();
        foreach (var block in listingBlocks)
        {
            if (chunks.Count == 0 ||
                chunks[^1].Sum(b => b.Length) + block.Length > MaxChunkChars)
            {
                chunks.Add(new List<string>());
            }
            chunks[^1].Add(block);
        }
        var summaries = new List<string>();

        // --- Fine-grained progress plumbing ---
        // totalRequests = one summary request per chunk + one per report section.
        var totalRequests = chunks.Count + SectionCount;
        var completedRequests = 0;
        string currentStep = string.Empty;
        int currentMaxTokens = 0;

        void BeginStep(int maxTokens, string name)
        {
            currentStep = name;
            currentMaxTokens = maxTokens;
            AnalysisProgress?.Invoke(this, new AnalysisProgressEventArgs(
                runId, completedRequests, totalRequests, 0, maxTokens, name));
        }

        void EndStep()
        {
            completedRequests++;
            AnalysisProgress?.Invoke(this, new AnalysisProgressEventArgs(
                runId, completedRequests, totalRequests, 0, currentMaxTokens, currentStep));
        }

        // Reports streamed token counts of the active request up to the UI.
        var progress = new Progress<string>(msg =>
        {
            Report(msg);
            var m = TokenCountRegex.Match(msg);
            if (m.Success)
            {
                AnalysisProgress?.Invoke(this, new AnalysisProgressEventArgs(
                    runId, completedRequests, totalRequests,
                    int.Parse(m.Groups[1].Value), currentMaxTokens, currentStep));
            }
        });

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var batchText = string.Join("\n\n", chunks[i]);
            BeginStep(ChunkSummaryTokens, $"Summarising API batch {i + 1} of {chunks.Count}");
            Report($"Summarising chunk {i + 1}/{chunks.Count} ({chunks[i].Count} APIs)...");

            var chunkPrompt = $@"You are a market research analyst reviewing RapidAPI listings.
Each listing below has three parts: its identity, an Overview (what the API does), and
real Customer reviews & discussion from its RapidAPI page. The reviews are the most
valuable signal — capture recurring complaints, praise, what users cannot get today, and
anything indicating willingness to pay. Keep it concise — a few bullet points per API.

Listings for ""{keyword}"":
{batchText}

Summary of notable APIs, themes and customer feedback:";

            var summary = await analyzer.CompleteAsync(chunkPrompt, ChunkSummaryTokens, progress, ct);
            summaries.Add(summary);
            EndStep();
        }

        // Step 2: Combine all chunk summaries into a condensed context.
        var condensedContext = string.Join("\n\n---\n\n", summaries);
        Report($"Condensed {listings.Count} listings into {summaries.Count} summary chunk(s).");

        // Step 3: Generate each report section as a separate, smaller request.
        var sections = BuildSectionPrompts(keyword, condensedContext);

        var report = new StringBuilder();
        report.AppendLine($"# Gap-Analysis Report: {keyword}");
        report.AppendLine();

        foreach (var (title, prompt, maxTokens) in sections)
        {
            ct.ThrowIfCancellationRequested();
            BeginStep(maxTokens, $"Writing {title}");
            Report($"Generating {title}...");
            var sectionText = await analyzer.CompleteAsync(prompt, maxTokens, progress, ct);
            report.AppendLine($"## {title}");
            report.AppendLine();
            report.AppendLine(sectionText.Trim());
            report.AppendLine();
            EndStep();
        }

        Report("Gap-analysis report complete.");
        return report.ToString().Trim();
    }

    /// <summary>
    /// Builds the prompt + token budget for each report section.
    /// Each section is generated in its own LLM request so the model never has to
    /// produce the entire report in a single 1200-token pass.
    /// </summary>
    private static (string Title, string Prompt, int MaxTokens)[] BuildSectionPrompts(string keyword, string condensedContext)
    {
        return new[]
        {
            ("1. Market Overview",
             $@"You are a market research analyst. Based on the following summarised
competitor APIs found on RapidAPI for keyword ""{keyword}"", write a concise
2-3 sentence market overview describing the overall market and key players.

{condensedContext}

Market Overview:", 200),

            ("2. Competitor Landscape (table)",
             $@"Based on the following summarised competitor APIs on RapidAPI for
keyword ""{keyword}"", create a markdown table with columns:
| # | API | Provider | Focus | Notes |
List up to 12 significant competitors.

{condensedContext}

Competitor Landscape:", 500),

            ("3. Gaps & Underserved Needs",
             $@"Based on the following competitor APIs on RapidAPI for keyword
""{keyword}"", identify 3-5 specific market gaps and underserved needs that present
clear opportunities for new API providers. Where customer comments were included in the
context, ground each gap in what users actually complained about or requested.

{condensedContext}

Gaps & Underserved Needs:", 400),

            ("4. Recommended APIs to Build (top 3)",
             $@"Based on the gaps identified and the customer feedback found in the
competitor landscape for ""{keyword}"" on RapidAPI, recommend 3 innovative API product
ideas to build. Prefer ideas that directly address pain points customers complained about.
For each: (1) target users, (2) key endpoints, (3) differentiation.

{condensedContext}

Recommended APIs:", 600),

            ("5. Risks",
             $@"Based on the following competitor landscape for ""{keyword}"" APIs,
identify 3 key risks for building APIs in this space (e.g. platform dependency,
rate limits, market saturation).

{condensedContext}

Risks:", 300),
        };
    }
}