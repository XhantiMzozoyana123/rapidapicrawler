using Hangfire;
using RapidApiCrawler.Application;
using RapidApiCrawler.Infrastructure;

namespace RapidApiCrawler.Web.Services;

/// <summary>
/// Hangfire job entry point for running a crawler search. Used by both the recurring
/// cron job and the manual "Start Crawl" button. Progress is streamed into
/// <see cref="CrawlJobCoordinator"/> keyed by <paramref name="jobId"/> so the UI can
/// poll it by job id.
/// </summary>
public class CrawlJobService
{
    private readonly CrawlOrchestrator _orchestrator;
    private readonly CrawlJobCoordinator _coordinator;
    private readonly ScraperOptions _scraperOptions;
    private readonly AnalysisProgressService _analysisProgress;
    private readonly ILogger<CrawlJobService> _logger;

    public CrawlJobService(
        CrawlOrchestrator orchestrator,
        CrawlJobCoordinator coordinator,
        ScraperOptions scraperOptions,
        AnalysisProgressService analysisProgress,
        ILogger<CrawlJobService> logger)
    {
        _orchestrator = orchestrator;
        _coordinator = coordinator;
        _scraperOptions = scraperOptions;
        _analysisProgress = analysisProgress;
        _logger = logger;
    }

    /// <summary>
    /// Runs a crawl and streams progress. Marked so Hangfire never auto-retries a crawl
    /// (they are expensive and may re-scrape the same data).
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunCrawlAsync(string jobId, string keyword, bool analyze, int maxListings, bool headless)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            _logger.LogWarning("Crawl job {JobId} skipped: empty keyword.", jobId);
            return;
        }

        // Apply THIS job's headless preference immediately before any browser use.
        // (Passed explicitly per job — never read from shared mutable state earlier.)
        SetHeadless(jobId, headless);

        var kw = keyword.Trim();

        // Atomically claim the job — guarantees only one crawl runs at a time so the
        // shared Playwright browser/orchestrator is never used concurrently.
        if (!_coordinator.TryBegin(jobId, kw))
        {
            _logger.LogWarning("Crawl job {JobId} skipped: a crawl is already running.", jobId);
            return;
        }

        // Handler stays synchronous: Progress.Invoke is synchronous and Append is thread-safe.
        void Handler(object? _, ProgressEventArgs e) => _coordinator.Append(jobId, e.Message);

        _orchestrator.Progress += Handler;
        try
        {
            _logger.LogInformation("Starting crawl '{Keyword}' (job {JobId}).", kw, jobId);
            var run = await _orchestrator.RunAsync(kw, analyze, CancellationToken.None, maxListings);
            if (analyze) _analysisProgress.MarkCompleted(run.Id);
            _coordinator.Complete(jobId, run.Id, run.ListingsFound, run.PagesCrawled);
            _logger.LogInformation("Crawl job {JobId} completed run #{RunId} ({Listings} listings, {Pages} pages).",
                jobId, run.Id, run.ListingsFound, run.PagesCrawled);
        }
        catch (Exception ex)
        {
            _coordinator.Fail(jobId, ex.Message);
            _logger.LogError(ex, "Crawl job {JobId} failed.", jobId);
            throw; // surface to Hangfire so the Dashboard reflects the failure
        }
        finally
        {
            _orchestrator.Progress -= Handler;
        }
    }

    /// <summary>Hangfire entry point: generate (or regenerate) the AI gap-analysis report for an existing crawl run.</summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAnalysisAsync(string jobId, int runId)
    {
        void Handler(object? _, ProgressEventArgs e) => _coordinator.Append(jobId, e.Message);

        _orchestrator.Progress += Handler;
        _analysisProgress.Start(runId);
        try
        {
            _logger.LogInformation("Starting gap-analysis for run #{RunId} (job {JobId}).", runId, jobId);
            var text = await _orchestrator.AnalyzeExistingRunAsync(runId, CancellationToken.None);
            _analysisProgress.MarkCompleted(runId);
            _coordinator.Complete(jobId, runId, 0, 0);
            _coordinator.Append(jobId, "=== REPORT READY — see the Report page ===");
            _logger.LogInformation("Gap-analysis job {JobId} completed ({Length} chars).", jobId, text.Length);
        }
        catch (Exception ex)
        {
            _analysisProgress.MarkFailed(runId, ex.Message);
            _coordinator.Fail(jobId, ex.Message);
            _logger.LogError(ex, "Gap-analysis job {JobId} failed.", jobId);
            throw;
        }
        finally
        {
            _orchestrator.Progress -= Handler;
        }
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task RunPopularCrawlAsync(string jobId, bool analyze, int maxListings, bool headless)
    {
        // Apply THIS job's headless preference immediately before any browser use.
        SetHeadless(jobId, headless);

        // Atomically claim the job — guarantees only one crawl runs at a time so the
        // shared Playwright browser/orchestrator is never used concurrently.
        if (!_coordinator.TryBegin(jobId, "popular-apis"))
        {
            _logger.LogWarning("Popular crawl job {JobId} skipped: a crawl is already running.", jobId);
            return;
        }

        void Handler(object? _, ProgressEventArgs e) => _coordinator.Append(jobId, e.Message);

        _orchestrator.Progress += Handler;
        try
        {
            _logger.LogInformation("Starting 'Popular APIs' scrape (job {JobId}).", jobId);
            var run = await _orchestrator.RunPopularAsync(analyze, CancellationToken.None, maxListings);
            if (analyze) _analysisProgress.MarkCompleted(run.Id);
            _coordinator.Complete(jobId, run.Id, run.ListingsFound, run.PagesCrawled);
            _logger.LogInformation("Popular crawl job {JobId} completed run #{RunId}.", jobId, run.Id);
        }
        catch (Exception ex)
        {
            _coordinator.Fail(jobId, ex.Message);
            _logger.LogError(ex, "Popular crawl job {JobId} failed.", jobId);
            throw;
        }
        finally
        {
            _orchestrator.Progress -= Handler;
        }
    }

    /// <summary>
    /// Applies THIS job's headless flag to the shared scraper options. The Playwright
    /// client reads it on its next browser use and relaunches if the mode changed.
    /// </summary>
        private void SetHeadless(string jobId, bool headless)
    {
        _scraperOptions.Headless = headless;
        _logger.LogInformation("Crawl job {JobId} using {Mode} browser.", jobId,
            headless ? "HEADLESS" : "HEADED (visible)");
    }

    /// <summary>
    /// Hangfire entry point: generate (or regenerate) the keyword strategy for an existing
    /// crawl run. Wired to the standalone /KeywordStrategy controller's Generate button.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunKeywordStrategyAsync(string jobId, int runId)
    {
        void Handler(object? _, ProgressEventArgs e) => _coordinator.Append(jobId, e.Message);

        _orchestrator.Progress += Handler;
        _analysisProgress.Start(runId);
        try
        {
            _logger.LogInformation("Starting keyword strategy for run #{RunId} (job {JobId}).", runId, jobId);
            var text = await _orchestrator.GenerateKeywordStrategyAsync(runId, CancellationToken.None);
            _analysisProgress.MarkCompleted(runId);
            _coordinator.Complete(jobId, runId, 0, 0);
            _coordinator.Append(jobId, "=== KEYWORD STRATEGY READY — see the Keyword Strategy page ===");
            _logger.LogInformation("Keyword strategy job {JobId} completed ({Length} chars).", jobId, text.Length);
        }
        catch (Exception ex)
        {
            _analysisProgress.MarkFailed(runId, ex.Message);
            _coordinator.Fail(jobId, ex.Message);
            _logger.LogError(ex, "Keyword strategy job {JobId} failed.", jobId);
            throw;
        }
        finally
        {
            _orchestrator.Progress -= Handler;
        }
    }
/// <summary>
    /// Hangfire entry point: generate (or regenerate) the SEO listing documentation for one
    /// recommended opportunity on a run. Wired to the /SeoListing controller's Generate button.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunSeoDocGenerationAsync(string jobId, int runId, int ideaNumber)
    {
        void Handler(object? _, ProgressEventArgs e) => _coordinator.Append(jobId, e.Message);

        _orchestrator.Progress += Handler;
        _analysisProgress.Start(runId);
        try
        {
            _logger.LogInformation("Starting SEO doc generation for run #{RunId} idea #{Idea} (job {JobId}).",
                runId, ideaNumber, jobId);
            var text = await _orchestrator.GenerateSeoDocumentationAsync(runId, ideaNumber, CancellationToken.None);
            _analysisProgress.MarkCompleted(runId);
            _coordinator.Complete(jobId, runId, 0, 0);
            _coordinator.Append(jobId, $"=== SEO DOCUMENTATION READY for idea #{ideaNumber} — see the SEO Listing page ===");
            _logger.LogInformation("SEO doc job {JobId} completed ({Length} chars).", jobId, text.Length);
        }
        catch (Exception ex)
        {
            _analysisProgress.MarkFailed(runId, ex.Message);
            _coordinator.Fail(jobId, ex.Message);
            _logger.LogError(ex, "SEO doc job {JobId} failed.", jobId);
            throw;
        }
        finally
        {
            _orchestrator.Progress -= Handler;
        }
    }
}