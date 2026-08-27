using System.Text;
using System.Text.Json;
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
    private const int SectionCount = 7;

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

        // ---- Phase 2: Customer Voice engine ----
        // Convert unstructured discussions into structured CustomerFeedback rows (DB-backed),
        // then aggregate counts/severity deterministically. These aggregates become verified
        // facts for every later section — the LLM interprets them, it does not invent them.
        var (feedbackRows, feedbackRequests) =
            await ExtractCustomerFeedbackAsync(runId, listings, commentsTextByListing, ct);
        Report($"Customer Voice: {feedbackRows.Count} structured signals extracted " +
               $"({feedbackRequests} LLM call(s)).");

        var verifiedVoiceFacts = BuildVerifiedVoiceFacts(feedbackRows);

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
        // totalRequests = one summary request per chunk + one per report section
        // + one per customer-voice extraction batch.
        var totalRequests = chunks.Count + SectionCount + feedbackRequests;
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
        // Deterministic logic lives HERE, not in the model:
        //  - classification counts/percentages parsed from the landscape table summary
        //    and re-injected as authoritative facts into every later prompt;
        //  - Opportunity Scores computed from the model's raw component assessments
        //    with fixed weights, plus BUILD/INVESTIGATE/MONITOR/AVOID verdicts.
        var sections = BuildSectionPrompts(keyword);
        var verifiedFacts = verifiedVoiceFacts;

        var report = new StringBuilder();
        report.AppendLine($"# Gap-Analysis Report: {keyword}");
        report.AppendLine($"_Run #{runId} · {listings.Count} APIs analysed · " +
                          $"{overviewTextByListing.Count} overviews · {commentsTextByListing.Count} listings with customer discussions · " +
                          $"generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC_");
        report.AppendLine();

        foreach (var (title, promptTemplate, maxTokens) in sections)
        {
            ct.ThrowIfCancellationRequested();
            var prompt = promptTemplate
                .Replace("{verifiedFacts}", verifiedFacts)
                .Replace("{condensedContext}", condensedContext);

            BeginStep(maxTokens, $"Writing {title}");
            Report($"Generating {title}...");

            var sectionText = await analyzer.CompleteAsync(prompt, maxTokens, progress, ct);

            // Validate; if the response looks incomplete/malformed, regenerate with
            // corrective feedback instead of saving a broken section.
            for (var attempt = 2; !ValidateSection(title, sectionText) && attempt <= 3; attempt++)
            {
                Report($"{title}: output failed validation (attempt {attempt - 1}) — regenerating...");
                sectionText = await analyzer.CompleteAsync(
                    prompt + "\n\nIMPORTANT: Your previous response was rejected because it was incomplete, " +
                    "truncated mid-sentence, or violated the format instructions above. Produce the FULL " +
                    "section this time, follow the exact requested structure/counts and heading format, " +
                    "and finish every sentence.",
                    maxTokens, progress, ct);
            }

            var clean = sectionText.Trim();

            // ---- Deterministic post-processing (C#, not the LLM) ----
            if (title.Contains("Competitor Landscape", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(clean,
                    @"CLASSIFICATION_SUMMARY:\s*direct=(\d+)\s+adjacent=(\d+)\s+irrelevant=(\d+)",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    int direct = int.Parse(m.Groups[1].Value),
                        adjacent = int.Parse(m.Groups[2].Value),
                        irrelevant = int.Parse(m.Groups[3].Value);
                    int total = direct + adjacent + irrelevant;
                    if (total > 0)
                    {
                        double pctDirectAdj = Math.Round(100.0 * (direct + adjacent) / total, 1);
                        verifiedFacts = verifiedVoiceFacts +
                            $"\nVERIFIED CLASSIFICATION COUNTS (computed programmatically from the classified table — cite these figures exactly): " +
                            $"{total} APIs listed: DIRECT={direct}, ADJACENT={adjacent}, IRRELEVANT={irrelevant}. " +
                            $"Relevant (direct+adjacent) = {pctDirectAdj}% of the listed set.";
                        Report($"Verified classification parsed: {direct}D/{adjacent}A/{irrelevant}I ({pctDirectAdj}% relevant).");
                    }

                    // Keep the machine-readable line out of the human report but leave a friendly footer.
                    clean = Regex.Replace(clean,
                        @"\n?CLASSIFICATION_SUMMARY:[^\n]*",
                        $"\n_Classification totals: {direct} direct · {adjacent} adjacent · {irrelevant} irrelevant_");
                }
            }

            if (title.Contains("Recommended API Opportunities", StringComparison.OrdinalIgnoreCase))
            {
                clean = RecomputeOpportunityScores(clean);
            }

            report.AppendLine($"## {title}");
            report.AppendLine();
            report.AppendLine(clean);
            report.AppendLine();
            EndStep();
        }

        Report("Gap-analysis report complete.");
        return report.ToString().Trim();
    }

    /// <summary>
    /// Deterministic scoring engine. The LLM supplies raw component assessments
    /// (Demand, Customer Pain, Competition, Market Saturation, Build Difficulty,
    /// Evidence Strength — all 'higher = more of that thing'); this method computes the
    /// final Opportunity Score with FIXED weights (beneficial metrics count positively,
    /// Competition/Saturation/Difficulty are inverted) and assigns a verdict:
    /// BUILD / INVESTIGATE / MONITOR / AVOID. Removes any AI-computed score so numbers
    /// in the saved report always come from this code, never from the model.
    /// Weights: Demand .30 · Pain .25 · Evidence .20 · Competition .10 · Saturation .075 · Difficulty .075.
    /// </summary>
    private static string RecomputeOpportunityScores(string text)
    {
        static double? Extract(string source, string metric) =>
            Regex.Match(source, Regex.Escape(metric) + @"\s*(?:\([^)]*\))?\s*[:=]?\s*(\d(?:\.\d+)?)\s*/\s*10",
                    RegexOptions.IgnoreCase) is { Success: true } m
                ? double.Parse(m.Groups[1].Value)
                : null;

        // Split into per-idea blocks at medal headings; rebuild with computed scores.
        var parts = Regex.Split(text, @"(?=^###?\s*[🥇🥈🥉])", RegexOptions.Multiline);
        if (parts.Length <= 1) return text;

        var sb = new StringBuilder();
        sb.Append(parts[0]);
        for (var i = 1; i < parts.Length; i++)
        {
            var block = parts[i];

            // Strip any opportunity-score line the model wrote itself.
            block = Regex.Replace(block,
                @"\n\s*(?:[-*]\s*)?\*{0,2}Opportunity Score[^\n]*", string.Empty);

            double? demand = Extract(block, "Demand"),
                    pain = Extract(block, "Customer Pain"),
                    competition = Extract(block, "Competition"),
                    saturation = Extract(block, "Market Saturation"),
                    difficulty = Extract(block, "Build Difficulty"),
                    evidence = Extract(block, "Evidence Strength");

            if (demand.HasValue && pain.HasValue && competition.HasValue &&
                saturation.HasValue && difficulty.HasValue && evidence.HasValue)
            {
                var score = Math.Round(
                    0.300 * demand.Value +
                    0.250 * pain.Value +
                    0.200 * evidence.Value +
                    0.100 * (10 - competition.Value) +
                    0.075 * (10 - saturation.Value) +
                    0.075 * (10 - difficulty.Value), 1);

                var verdict = score switch
                {
                    >= 8.0 => "**BUILD**",
                    >= 6.5 => "**INVESTIGATE**",
                    >= 4.5 => "**MONITOR**",
                    _ => "**AVOID**"
                };

                block = block.TrimEnd() +
                        $"\n- **Computed Opportunity Score: {score}/10 — Verdict: {verdict}**" +
                        "\n_(calculated by the application from component scores: Demand 30%, Pain 25%, Evidence 20%, inverted Competition/Saturation/Difficulty 25% combined)_";
            }

            sb.Append(block);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Heuristic quality gate applied to each generated report section before it is saved.
    /// Rejects empty responses, ultra-short responses (likely refusals), text that ends
    /// mid-sentence (token-budget truncation), template placeholders left unfilled, count
    /// violations (gaps must list exactly 4 items), literal "Idea N" formatting bugs, and
    /// opportunities sections without at least two ranked ideas.
    /// </summary>
    private static bool ValidateSection(string title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Length < 150) return false;
        if (trimmed.Contains("{condensedContext}") || trimmed.Contains("{verifiedFacts}"))
            return false; // template placeholder leaked into output

        // Truncation heuristic: the final line should terminate cleanly.
        var lastLine = trimmed.Split('\n').LastOrDefault()?.Trim() ?? string.Empty;
        if (lastLine.Length > 25 && !Regex.IsMatch(lastLine, @"[.!?:;\)\]""*`\d_]$"))
            return false;

        // Formatting bug: literal 'Idea N' placeholder instead of sequential numbering.
        if (title.Contains("Recommended", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(trimmed, @"\bIdea N\b"))
            return false;

        if (title.Contains("Gaps", StringComparison.OrdinalIgnoreCase))
        {
            var items = Regex.Matches(trimmed, @"(?m)^[-*•]?\s*\*{0,2}\s*Gap\s+\d+:", RegexOptions.IgnoreCase).Count;
            if (items != 4) return false;
        }

        if (title.Contains("Recommended", StringComparison.OrdinalIgnoreCase))
        {
            var ideas = Regex.Matches(trimmed, @"^[#*\s]*[🥇🥈🥉]\s*Idea\s*\d+", RegexOptions.Multiline).Count;
            if (ideas < 2 || ideas > 3) return false;
        }

        return true;
    }

    /// <summary>
    /// Customer Voice extraction: sends captured discussion text per listing (batched) to
    /// the LLM and parses the returned JSON array into structured <see cref="CustomerFeedback"/>
    /// rows persisted in the database. Aggregation of those rows happens in C# only.
    /// </summary>
    private async Task<(List<CustomerFeedback> Items, int Requests)> ExtractCustomerFeedbackAsync(
        int runId,
        List<ApiListing> listings,
        IReadOnlyDictionary<int, string> commentsTextByListing,
        CancellationToken ct)
    {
        var items = new List<CustomerFeedback>();
        var withComments = listings.Where(l => commentsTextByListing.ContainsKey(l.Id)).ToList();
        if (withComments.Count == 0) return (items, 0);

        var slugToListing = listings
            .GroupBy(l => l.ApiSlug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var requestCount = 0;
        foreach (var batch in withComments.Chunk(5))
        {
            ct.ThrowIfCancellationRequested();
            var payload = string.Join("\n\n", batch.Select(l =>
                $"API slug: {l.ApiSlug}\nName: {l.Name}\nCustomer discussion text: {commentsTextByListing[l.Id]}"));

            var prompt = $@"You are a customer-voice analyst. Below are captured customer
discussions for several RapidAPI APIs. Extract EVERY distinct signal into a JSON array.
Classify each item:
- sentiment: positive | negative | neutral | question | request
- topic: performance | pricing | documentation | reliability | integration |
  developer-experience | feature-gap | other
- painPoint: short pain phrase (or empty string if not a complaint)
- featureRequest: short request phrase (or empty string if not a request)
- severity: 0.0-1.0 (impact on the customer)
- quote: a short verbatim snippet from the text supporting this item

Return ONLY the JSON array — no commentary, no markdown fences.
Example item: {{""slug"":""youtube138"",""sentiment"":""negative"",""topic"":""performance"",""painPoint"":""slow bulk requests"",""featureRequest"":"""",""severity"":0.8,""quote"":""painfully slow when I make multiple requests""}}

{payload}";

            requestCount++;
            string raw;
            try
            {
                raw = await analyzer.CompleteAsync(prompt, 1200, NullProgress.Instance, ct);
            }
            catch (Exception ex)
            {
                Report($"WARNING: customer-voice extraction batch failed ({ex.Message}) — skipping.");
                continue;
            }

            foreach (var item in ParseFeedbackJson(raw, slugToListing))
                items.Add(item);
        }

        try
        {
            await repository.ReplaceCustomerFeedbackAsync(runId, items);
        }
        catch (Exception ex)
        {
            Report($"WARNING: could not persist customer feedback ({ex.Message}).");
        }
        return (items, requestCount);
    }
    private static List<CustomerFeedback> ParseFeedbackJson(
        string raw, Dictionary<string, ApiListing> slugToListing)
    {
        var results = new List<CustomerFeedback>();
        try
        {
            var start = raw.IndexOf('[');
            var end = raw.LastIndexOf(']');
            if (start < 0 || end <= start) return results;
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string Get(string name) =>
                    el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString()?.Trim() ?? string.Empty : string.Empty;

                var slug = Get("slug");
                if (!slugToListing.TryGetValue(slug, out var listing)) continue;

                var severity = el.TryGetProperty("severity", out var sev) &&
                               sev.ValueKind == JsonValueKind.Number
                    ? sev.GetDouble() : 0.5;

                results.Add(new CustomerFeedback
                {
                    ListingId = listing.Id,
                    Sentiment = Get("sentiment").ToLowerInvariant() is { Length: > 0 } s ? s : "neutral",
                    Topic = Get("topic").ToLowerInvariant() is { Length: > 0 } t ? t : "other",
                    PainPoint = Get("painPoint"),
                    FeatureRequest = Get("featureRequest"),
                    Severity = Math.Clamp(severity, 0, 1),
                    Quote = Get("quote") is { Length: > 300 } q ? q[..300] : Get("quote"),
                });
            }
        }
        catch
        {
            // Malformed JSON from the model — skip this batch; aggregation still works
            // with whatever batches parsed.
        }
        return results;
    }

    /// <summary>Deterministic aggregation of structured feedback into verified signal facts.</summary>
    private static string BuildVerifiedVoiceFacts(List<CustomerFeedback> rows)
    {
        if (rows.Count == 0)
            return "VERIFIED CUSTOMER SIGNALS: no structured customer feedback was extracted for this run.";

        var sb = new StringBuilder();
        sb.AppendLine($"VERIFIED CUSTOMER SIGNALS (computed programmatically from {rows.Count} extracted feedback rows — interpret these exact numbers; do not invent others):");

        foreach (var p in rows.Where(f => !string.IsNullOrWhiteSpace(f.PainPoint))
                     .GroupBy(f => f.PainPoint.ToLowerInvariant())
                     .Select(g => new { Name = g.First().PainPoint, Count = g.Count(), Sev = g.Average(f => f.Severity) })
                     .OrderByDescending(p => p.Count).Take(10))
            sb.AppendLine($"  - PAIN: {p.Name}: {p.Count} mention(s), severity {p.Sev:0.00}");

        foreach (var r in rows.Where(f => !string.IsNullOrWhiteSpace(f.FeatureRequest))
                     .GroupBy(f => f.FeatureRequest.ToLowerInvariant())
                     .Select(g => new { Name = g.First().FeatureRequest, Count = g.Count(), Sev = g.Average(f => f.Severity) })
                     .OrderByDescending(r => r.Count).Take(10))
            sb.AppendLine($"  - REQUEST: {r.Name}: {r.Count} request(s), demand weight {r.Sev:0.00}");

        sb.AppendLine("BY TOPIC: " + string.Join(", ",
            rows.GroupBy(f => f.Topic).Select(g => $"{g.Key}={g.Count()}").OrderByDescending(s => s)));
        sb.AppendLine($"Signals cover {rows.Select(f => f.ListingId).Distinct().Count()} distinct API(s).");
        return sb.ToString();
    }

    /// <summary>Shared no-op progress sink for auxiliary LLM stages.</summary>
    private sealed class NullProgress : IProgress<string>
    {
        public static readonly NullProgress Instance = new();
        public void Report(string value) { }
    }

    private static (string Title, string Prompt, int MaxTokens)[] BuildSectionPrompts(string keyword)
    {
        const string GroundingRules = @"
CRITICAL RULES:
1. EVIDENCE FIRST. Tag every claim [OBSERVED] (from actual captured reviews) or
   [INFERRED] (your interpretation where no direct review exists).
2. NEVER invent complaints, requests, percentages, or counts. If the context lacks customer
   feedback on a topic, write 'No direct customer evidence found'. Any number you state
   MUST appear in the context itself. Verified figures supplied separately are authoritative.
3. USE ONLY RELEVANT EVIDENCE. If an API was classified IRRELEVANT you may not cite its
   reviews, features or problems anywhere in your analysis.
4. You may reference ONLY the context below. No outside knowledge about these markets.";

        const string ScoreScale = @"
SCORING SCALE — all component scores 0-10, direction ALWAYS 'higher value means more of
the thing named' (Competition 9/10 = very crowded market; Saturation 2/10 = wide open):
- Demand: how many customers want this capability.
- Customer Pain: how severe the observed complaints are.
- Competition: level of competition (higher = MORE competitors).
- Market Saturation: degree of saturation (higher = MORE saturated).
- Build Difficulty rubric: 1-3 simple REST wrapper over existing services · 4-6 moderate
  processing/caching/scaling · 7-8 complex infrastructure (browsers, video/media
  pipelines, queues) · 9-10 heavy platform dependency or regulatory exposure.";
        // NOTE: Do NOT compute an overall Opportunity Score yourself — the application
        // calculates it deterministically from your components after parsing.";

        return new[]
        {
            // Generated FIRST so the relevance classification becomes verified input for
            // every following section (the C# layer parses the summary line and re-injects it).
            ("1. Competitor Landscape (classified table)",
             $@"Based on the summarised APIs crawled from RapidAPI for keyword ""{keyword}"",
create a markdown table with EXACTLY these columns:
| # | API | Provider | Relevance | Focus | Customer Sentiment |
Relevance must be one of: DIRECT / ADJACENT / IRRELEVANT (Focus column: max 10 words why).
Include up to 12 significant APIs. In Customer Sentiment, summarise that API's reviews if
any were captured ('no reviews captured' otherwise).
After the table, on its own final line, print the exact machine-count of what you listed:
CLASSIFICATION_SUMMARY: direct=<N> adjacent=<M> irrelevant=<K>{GroundingRules}

{{condensedContext}}

Competitor Landscape:", 700),

            ("2. Customer Voice Analysis (structured signals)",
             $@"Below are VERIFIED CUSTOMER SIGNALS — pain points, feature requests and topic
breakdowns computed programmatically from structured extraction of real captured reviews.
Your job is INTERPRETATION ONLY: explain what these numbers mean for someone entering the
""{keyword}"" space, which pains are most strategically valuable to solve, which requests
signal willingness to pay, and where signals are too sparse to be trusted. Every number you
cite MUST match the verified counts. Do not add new pain points or requests that are not in
the verified list.{GroundingRules}

{{verifiedFacts}}

Customer Voice Analysis:", 500),

            ("3. Market Overview",
             $@"You are a rigorous market research analyst writing the market overview for
keyword ""{keyword}"" (3-5 sentences). If VERIFIED CLASSIFICATION COUNTS were supplied,
cite those exact numbers as proportions of relevant vs non-relevant APIs — they were
computed programmatically and outrank your estimates. Never invent percentages.{GroundingRules}

{{verifiedFacts}}

{{condensedContext}}

Market Overview:", 300),

            ("4. Gaps & Underserved Needs (evidence-ranked)",
             $@"Identify EXACTLY 4 specific market gaps for keyword ""{keyword}"" on RapidAPI.
Use ONLY APIs classified DIRECT or ADJACENT — their reviews may be cited; nothing from an
IRRELEVANT API may support any gap. For EACH gap output:
- **Gap N: <name>**
  - Evidence: <paraphrase actual customer complaints WITH the counts stated in the context,
    e.g. '3 reviews across 2 APIs mention quota failures'> or 'No direct customer evidence found'
  - Interpretation: <your analysis of why this gap exists>
  - Opportunity hypothesis: <what could be built, framed as a hypothesis>
Order gaps by strength of supporting evidence (strongest OBSERVED first).{GroundingRules}

{{verifiedFacts}}

{{condensedContext}}

Gaps & Underserved Needs:", 600),

            ("5. Recommended API Opportunities (evidence-scored)",
             $@"Propose API product opportunities for ""{keyword}"", ranked best-first. Produce
TWO OR THREE ideas — never pad to three with a weakly-supported one; if only two have real
evidence behind them, return two and say so. Format EACH idea exactly:
### 🥇 Idea 1: <name>   (then 🥈 Idea 2:, 🥉 Idea 3: — always sequential numbers, never 'Idea N')
- Evidence base: <observed complaints/patterns WITH counts; write 'Weak evidence' plainly
  if supported only by inference>
- Target users / Key endpoints / Differentiation
- Component scores: Demand: X/10 · Customer Pain: X/10 · Competition: X/10 ·
  Market Saturation: X/10 · Build Difficulty: X/10 · Evidence Strength: X/10

Base opportunities ONLY on DIRECT/ADJACENT APIs and their captured reviews. Components only
—the application computes the final Opportunity Score and verdict deterministically.
{GroundingRules}{ScoreScale}

{{verifiedFacts}}

{{condensedContext}}

Recommended API Opportunities:", 900),

            ("6. Risks & Data Limitations",
             $@"Identify 3 key risks for building APIs in the ""{keyword}"" space (platform
dependency, rate limits, saturation...) using only relevant-API evidence, THEN add a final
subsection '## Analysis Limitations' honestly listing what this report does NOT know: which
APIs lacked captured reviews, sample sizes available, and which conclusions rest purely on
inference.{GroundingRules}

{{verifiedFacts}}

{{condensedContext}}

Risks & Data Limitations:", 450),

            ("7. Estimated Market Size",
             $@"Estimate the total addressable market size for API products serving the
""{keyword}"" space. Follow this EXACT structure:
- Data basis (OBSERVED): <what the crawl actually shows — count of DIRECT+ADJACENT APIs,
  how many listings had customer discussions, total distinct complaint themes, ANY pricing
  figures literally mentioned in captured reviews. Use only numbers present in the context.>
- Bottom-up estimate [INFERRED]: <a plausible monthly-revenue range for a well-executed
  competitor, derived ONLY from your data basis above — e.g. review volume × assumed
  conversion willingness — showing your arithmetic explicitly>
- Market-level estimate [INFERRED]: <rough annual TAM band for this niche expressed as
  'LOW confidence' unless multiple independent signals agree; justify with one sentence
  per signal>
- Confidence level: HIGH / MEDIUM / LOW with one-sentence justification.
NEVER present a dollar figure as fact — every monetary figure here is an estimate labelled
[INFERRED], and you must NOT pull remembered statistics about these companies from outside
knowledge. If the crawled data contains no usable monetisation signals, say so plainly and
give only the widest possible inference band.{GroundingRules}

{{verifiedFacts}}

{{condensedContext}}

Estimated Market Size:", 500),
        };
    }
}