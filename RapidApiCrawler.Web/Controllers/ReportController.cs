using Hangfire;
using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;
using RapidApiCrawler.Web.Models;
using RapidApiCrawler.Web.Services;

namespace RapidApiCrawler.Web.Controllers;

    public class ReportController : Controller
    {
        private readonly ISearchRunRepository _repository;
        private readonly IBackgroundJobClient _backgroundJobs;
        private readonly IConfiguration _configuration;
        private readonly AnalysisProgressService _analysisProgress;

        public ReportController(
            ISearchRunRepository repository,
            IBackgroundJobClient backgroundJobs,
            IConfiguration configuration,
            AnalysisProgressService analysisProgress)
        {
            _repository = repository;
            _backgroundJobs = backgroundJobs;
            _configuration = configuration;
            _analysisProgress = analysisProgress;
        }

        /// <summary>
        /// JSON progress endpoint polled by the Report page: returns the live state of the
        /// AI gap-analysis for a run (percent, current step, running/completed/failed).
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

        public async Task<IActionResult> Index(int? runId)
        {
            var runs = await _repository.GetRunsAsync();
            ViewBag.Runs = runs;

            var latest = runId ?? runs.OrderByDescending(r => r.Id).FirstOrDefault()?.Id;
            var report = latest.HasValue ? await _repository.GetLatestReportAsync(latest.Value) : null;
            ViewBag.OllamaConfigured = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("OLLAMA_URL") ?? _configuration["Ollama:Url"]);

            return View(new ReportViewModel(latest, report));
        }

        /// <summary>Enqueues on-demand AI gap-analysis generation for an existing crawl run.</summary>
        [HttpPost]
        public IActionResult Generate(int runId)
        {
            var jobId = "analysis-" + Guid.NewGuid().ToString("N")[..12];
            _backgroundJobs.Enqueue<CrawlJobService>(svc => svc.RunAnalysisAsync(jobId, runId));
            TempData["AnalysisStarted"] = $"Report generation for run #{runId} started (job {jobId}). " +
                                          "A 7B local model typically takes 1–5 minutes; reload this page to see the result.";
            return RedirectToAction(nameof(Index), new { runId });
        }
    }