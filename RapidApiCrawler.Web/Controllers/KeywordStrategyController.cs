using Hangfire;
using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;
using RapidApiCrawler.Web.Models;
using RapidApiCrawler.Web.Services;

namespace RapidApiCrawler.Web.Controllers;

public class KeywordStrategyController : Controller
{
    private readonly ISearchRunRepository _repository;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly AnalysisProgressService _analysisProgress;

    public KeywordStrategyController(
        ISearchRunRepository repository,
        IBackgroundJobClient backgroundJobs,
        AnalysisProgressService analysisProgress)
    {
        _repository = repository;
        _backgroundJobs = backgroundJobs;
        _analysisProgress = analysisProgress;
    }

    /// <summary>
    /// GET: /KeywordStrategy?runId=N
    /// Shows the Keyword Strategy page for a run — separate from the gap-analysis
    /// report. Displays existing strategy text or a "Generate" button.
    /// </summary>
    public async Task<IActionResult> Index(int? runId)
    {
        var runs = await _repository.GetRunsAsync();
        ViewBag.Runs = runs;

        var latest = runId ?? runs.OrderByDescending(r => r.Id).FirstOrDefault()?.Id;
        var strategy = latest.HasValue
            ? await _repository.GetLatestReportAsync(latest.Value, "keyword-strategy")
            : null;
        ViewBag.OllamaConfigured = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("OLLAMA_URL") ??
            HttpContext.RequestServices.GetService<IConfiguration>()?["Ollama:Url"]);

        return View(new KeywordStrategyViewModel(latest, strategy));
    }

    /// <summary>
    /// POST: /KeywordStrategy/Generate?runId=N
    /// Enqueues the keyword-strategy analysis (same Hangfire pattern as the report).
    /// </summary>
    [HttpPost]
    public IActionResult Generate(int runId)
    {
        var jobId = "keyword-" + Guid.NewGuid().ToString("N")[..12];
        _backgroundJobs.Enqueue<CrawlJobService>(svc => svc.RunKeywordStrategyAsync(jobId, runId));
        TempData["StrategyStarted"] = $"Keyword strategy generation started for run #{runId} (job {jobId}). A 7B local model typically takes 1-5 minutes; reload this page to see the result.";
        return RedirectToAction(nameof(Index), new { runId });
    }

    /// <summary>
    /// JSON progress endpoint (reuses the same AnalysisProgressService infrastructure).
    /// </summary>
    [HttpGet]
    public IActionResult AnalysisProgress(int runId)
    {
        var state = _analysisProgress.Get(runId);
        return Json(new
        {
            status = state?.Status ?? "idle",
            percent = state?.Percent ?? 0,
            completedRequests = state?.CompletedRequests ?? 0,
            totalRequests = state?.TotalRequests ?? 0,
            stepPercent = state?.CurrentStepPercent ?? 0,
            step = state?.CurrentStep ?? "",
            message = state?.Message ?? "",
            updatedUtc = state?.UpdatedUtc,
        });
    }
}
