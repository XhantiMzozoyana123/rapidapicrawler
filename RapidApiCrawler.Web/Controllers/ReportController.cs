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

        public ReportController(
            ISearchRunRepository repository,
            IBackgroundJobClient backgroundJobs,
            IConfiguration configuration)
        {
            _repository = repository;
            _backgroundJobs = backgroundJobs;
            _configuration = configuration;
        }

        public async Task<IActionResult> Index(int? runId)
        {
            var runs = await _repository.GetRunsAsync();
            ViewBag.Runs = runs;

            var latest = runId ?? runs.OrderByDescending(r => r.Id).FirstOrDefault()?.Id;
            var report = latest.HasValue ? await _repository.GetLatestReportAsync(latest.Value) : null;
            ViewBag.LlamaConfigured = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("LLAMA_MODEL_PATH") ?? _configuration["Llama:ModelPath"]);

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