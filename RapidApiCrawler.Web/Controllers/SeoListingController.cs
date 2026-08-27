using Hangfire;
using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;
using RapidApiCrawler.Web.Models;
using RapidApiCrawler.Web.Services;

namespace RapidApiCrawler.Web.Controllers;

/// <summary>
/// Dedicated page for the SEO Listing Documentation feature: reads a run's saved gap-analysis
/// report, extracts its "Recommended API Opportunities", and lets the user generate an
/// SEO-optimised RapidAPI listing document for each one (name, descriptions, tags, keywords,
/// use cases, endpoints, README start, positioning). Each doc is saved independently as an
/// AnalysisReport with Model="seo-listing-{N}".
/// </summary>
public class SeoListingController : Controller
{
    private readonly ISearchRunRepository _repository;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly AnalysisProgressService _analysisProgress;

    public SeoListingController(
        ISearchRunRepository repository,
        IBackgroundJobClient backgroundJobs,
        AnalysisProgressService analysisProgress)
    {
        _repository = repository;
        _backgroundJobs = backgroundJobs;
        _analysisProgress = analysisProgress;
    }

    /// <summary>
    /// GET: /SeoListing?runId=N
    /// Loads the selected (or latest) run, pulls its saved gap-analysis report
    /// (Model="chunked-local-llm"), parses the recommended opportunities, and for each
    /// one loads any already-generated SEO doc (Model="seo-listing-{N}").
    /// </summary>
    public async Task<IActionResult> Index(int? runId)
    {
        var runs = await _repository.GetRunsAsync();
        ViewBag.Runs = runs;

        var latest = runId ?? runs.OrderByDescending(r => r.Id).FirstOrDefault()?.Id;
        if (!latest.HasValue)
            return View(new SeoListingViewModel(null, new List<SeoListingEntry>(), false, null));

        ViewBag.OllamaConfigured = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("OLLAMA_URL") ??
            HttpContext.RequestServices.GetService<IConfiguration>()?["Ollama:Url"]);

        string? report = null;
        try
        {
            report = await _repository.GetLatestReportAsync(latest.Value, "chunked-local-llm");
        }
        catch (Exception ex)
        {
            return View(new SeoListingViewModel(latest, new List<SeoListingEntry>(), false,
                $"Could not load the report for run #{latest.Value}: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(report))
            return View(new SeoListingViewModel(latest, new List<SeoListingEntry>(), false, null));

        var ideas = CrawlOrchestrator.ExtractRecommendedIdeas(report);
        var entries = new List<SeoListingEntry>();
        foreach (var idea in ideas)
        {
            var doc = await _repository.GetLatestReportAsync(latest.Value, $"seo-listing-{idea.Number}");
            entries.Add(new SeoListingEntry(idea.Number, idea.Name, doc, !string.IsNullOrWhiteSpace(doc)));
        }

        return View(new SeoListingViewModel(latest, entries, true, null));
    }

    /// <summary>
    /// POST: /SeoListing/Generate?runId=N&amp;idea=1
    /// Enqueues the SEO-documentation generation job for one recommended opportunity.
    /// </summary>
    [HttpPost]
    public IActionResult Generate(int runId, int idea)
    {
        var jobId = "seo-" + Guid.NewGuid().ToString("N")[..12];
        _backgroundJobs.Enqueue<CrawlJobService>(svc => svc.RunSeoDocGenerationAsync(jobId, runId, idea));
        TempData["SeoStarted"] = $"SEO documentation generation started for idea #{idea} on run #{runId} (job {jobId}). Reload this page to see the result.";
        return RedirectToAction(nameof(Index), new { runId });
    }

    /// <summary>JSON progress endpoint (reuses the AnalysisProgress infrastructure).</summary>
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