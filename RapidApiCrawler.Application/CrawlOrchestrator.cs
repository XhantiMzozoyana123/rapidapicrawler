using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Application;

public record ProgressEventArgs(string Message);

public class CrawlOrchestrator(
    IRapidApiClient client,
    ILlmAnalyzer analyzer,
    ISearchRunRepository repository)
{
    public event EventHandler<ProgressEventArgs>? Progress;

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
            Report("Requesting competitor gap analysis from Google AI...");
            var context = await BuildContext(run.Id);
            var reportText = await analyzer.AnalyzeAsync(keyword, context, ct);
            await repository.AddReportAsync(new AnalysisReport
            {
                SearchRunId = run.Id,
                Model = "llama-local",
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

    private async Task<string> BuildContext(int runId)
    {
        // Summarize listings so the LLM sees real signals.
        var listings = await repository.GetListingsAsync(runId);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Competitor APIs found for keyword search on RapidAPI:");
        foreach (var l in listings)
            sb.AppendLine($"- {l.Name} (provider: {l.Provider}, slug: {l.ApiSlug})");
        return sb.ToString();
    }
}