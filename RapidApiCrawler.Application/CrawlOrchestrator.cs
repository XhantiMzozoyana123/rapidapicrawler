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

        // Step 3: Generate each report section as a separate, smaller request — with
        // validation and one repair pass per section so truncated or malformed output
        // never reaches the saved report.
        var sections = BuildSectionPrompts(keyword, condensedContext);

        var report = new StringBuilder();
        report.AppendLine($"# Gap-Analysis Report: {keyword}");
        report.AppendLine($"_Run #{runId} · {listings.Count} APIs analysed · " +
                          $"{overviewTextByListing.Count} overviews · {commentsTextByListing.Count} listings with customer discussions · " +
                          $"generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC_");
        report.AppendLine();

        foreach (var (title, prompt, maxTokens) in sections)
        {
            ct.ThrowIfCancellationRequested();
            BeginStep(maxTokens, $"Writing {title}");
            Report($"Generating {title}...");

            var sectionText = await analyzer.CompleteAsync(prompt, maxTokens, progress, ct);

            // Validate; if the response looks incomplete/malformed, regenerate once with
            // corrective feedback instead of saving a broken section.
            for (var attempt = 2; !ValidateSection(title, sectionText) && attempt <= 3; attempt++)
            {
                Report($"{title}: output failed validation (attempt {attempt - 1}) — regenerating...");
                sectionText = await analyzer.CompleteAsync(
                    prompt + "\n\nIMPORTANT: Your previous response was rejected because it was incomplete, " +
                    "truncated mid-sentence, or violated the format instructions above. Produce the FULL " +
                    "section this time, follow the exact requested structure/counts, and finish every sentence.",
                    maxTokens, progress, ct);
            }

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
    /// Heuristic quality gate applied to each generated report section before it is saved.
    /// Rejects empty responses, ultra-short responses (likely refusals), text that ends
    /// mid-sentence (token-budget truncation), and count violations for structured
    /// sections (gaps must list 3–5 items, recommendations exactly 3).
    /// </summary>
    private static bool ValidateSection(string title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Length < 150) return false;

        // Truncation heuristic: the final line should terminate cleanly.
        var lastLine = trimmed.Split('\n').LastOrDefault()?.Trim() ?? string.Empty;
        if (lastLine.Length > 25 && !Regex.IsMatch(lastLine, @"[.!?:;\)\]""*`\d]$"))
            return false;

        if (title.Contains("Gaps", StringComparison.OrdinalIgnoreCase))
        {
            var items = Regex.Matches(trimmed, @"(?m)^\s*(?:\d+[\.\)]|[-*•])\s+\S").Count;
            if (items < 3 || items > 5) return false;
        }

        if (title.Contains("Recommended", StringComparison.OrdinalIgnoreCase))
        {
            var ideas = Regex.Matches(trimmed, @"(?m)^\s*(?:\d+[\.\)]|[🥇🥈🥉]|Idea\s*\d)", RegexOptions.IgnoreCase).Count;
            if (ideas < 3) return false;
        }

        return true;
    }

    /// <summary>
    /// Builds the prompt + token budget for each report section.
    /// Design principles: every claim is separated into OBSERVED evidence (from actual
    /// customer reviews) vs AI INTERPRETATION; competitors are classified by relevance
    /// instead of assuming everything in the search results competes; and every
    /// opportunity receives quantitative scores so ideas can be ranked.
    /// </summary>
    private static (string Title, string Prompt, int MaxTokens)[] BuildSectionPrompts(string keyword, string condensedContext)
    {
        const string GroundingRules = @"
CRITICAL RULES:
1. EVIDENCE FIRST. For every claim state whether it is:
   - [OBSERVED] — comes directly from actual customer reviews/discussion quoted in the context.
   - [INFERRED] — your own interpretation where no direct customer evidence exists.
2. NEVER invent customer complaints or requests. If the context contains no customer
   feedback on a topic, say 'No direct customer evidence found' instead of assuming demand.
3. QUANTIFY evidence whenever possible, e.g. '14 reviews mention quota problems'. If exact
   counts are unavailable, use approximate counts ('several', 'one review') — never inflate.
4. CLASSIFY competitors by relevance to the target problem space:
   Direct = same data/function for same customers · Adjacent = similar customers, different
   problem · Irrelevant = appeared only due to keyword overlap. Label each one.
You may only claim what the context supports.";

        return new[]
        {
            ("1. Market Overview",
             $@"You are a rigorous market research analyst. Based on the summarised APIs found
on RapidAPI for keyword ""{keyword}"", write a concise market overview (3-5 sentences):
what the space covers, who the key players are, and how many of the found APIs are actually
relevant to the core problem vs merely related.{GroundingRules}

{condensedContext}

Market Overview:", 300),

            ("2. Competitor Landscape (classified table)",
             $@"Based on the summarised APIs on RapidAPI for keyword ""{keyword}"", create a
markdown table with EXACTLY these columns:
| # | API | Provider | Relevance | Focus | Customer Sentiment |
Relevance must be one of: DIRECT / ADJACENT / IRRELEVANT (with a 5-word justification in
the Focus column). Include up to 12 significant APIs. Do NOT count IRRELEVANT ones as
competitors in later sections. In Customer Sentiment summarise that API's reviews if any
were captured ('no reviews captured' otherwise).{GroundingRules}

{condensedContext}

Competitor Landscape:", 700),

            ("3. Gaps & Underserved Needs (evidence-ranked)",
             $@"Identify EXACTLY 4 specific market gaps for keyword ""{keyword}"" on RapidAPI.
For EACH gap output this structure:
- **Gap N: <name>**
  - Evidence: <quote/paraphrase actual customer complaints with counts, e.g. '8 reviews
    across 3 APIs mention quota problems'> or 'No direct customer evidence found'
  - Interpretation: <your analysis of why this gap exists>
  - Opportunity hypothesis: <what could be built, framed as a hypothesis>
Order gaps by strength of supporting evidence. Exactly 4 gaps, no more, no less.{GroundingRules}

{condensedContext}

Gaps & Underserved Needs:", 600),

            ("4. Recommended APIs to Build (scored & ranked)",
             $@"Recommend exactly 3 API product ideas for ""{keyword}"", ranked best-first with
🥇 🥈 🥉 medals. For EACH idea output:
- **<Medal> Idea N: <name>**
  - Evidence base: <which observed complaints/patterns support this, WITH counts; write
    'Weak evidence' explicitly if only inference supports it>
  - Target users: ...
  - Key endpoints: ...
  - Differentiation: ...
  - Scores: Demand X/10 · Customer Pain X/10 · Competition (lower=better) X/10 ·
    Market Saturation (lower=better) X/10 · Build Difficulty X/10 · Evidence Strength X/10
  - **Opportunity Score: X.X/10** = weighted blend you justify in one sentence.
Scores must be internally consistent with the evidence (a weakly-evidenced idea cannot
score 9+ on Demand).{GroundingRules}

{condensedContext}

Recommended APIs:", 900),

            ("5. Risks & Data Limitations",
             $@"Identify 3 key risks for building APIs in the ""{keyword}"" space (platform
dependency, rate limits, saturation...), THEN add a final subsection '## Analysis
Limitations' honestly listing what this report does NOT know: which APIs lacked captured
reviews, sample sizes available, and where conclusions rest on inference rather than
evidence.{GroundingRules}

{condensedContext}

Risks & Data Limitations:", 450),
        };
    }
}