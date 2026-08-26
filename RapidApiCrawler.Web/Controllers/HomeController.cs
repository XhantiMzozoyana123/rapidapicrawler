using System.Diagnostics;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;
using RapidApiCrawler.Infrastructure;
using RapidApiCrawler.Web.Models;
using RapidApiCrawler.Web.Services;

namespace RapidApiCrawler.Web.Controllers;

public class HomeController : Controller
{
    private readonly ISearchRunRepository _repository;
    private readonly ScraperOptions _scraperOptions;
    private readonly CrawlJobCoordinator _coordinator;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IConfiguration _configuration;

    public const string CronJobId = "rapidapi-crawl";

    public HomeController(
        ISearchRunRepository repository,
        ScraperOptions scraperOptions,
        CrawlJobCoordinator coordinator,
        IBackgroundJobClient backgroundJobs,
        IConfiguration configuration)
    {
        _repository = repository;
        _scraperOptions = scraperOptions;
        _coordinator = coordinator;
        _backgroundJobs = backgroundJobs;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index(string? jobId)
    {
        var runs = await _repository.GetRunsAsync();
        ViewBag.SelectedJobId = jobId ?? _coordinator.Snapshot(null).JobId;
        ViewBag.Snapshot = _coordinator.Snapshot(jobId);
        ViewBag.Jobs = _coordinator.All();
        ViewBag.CronKeyword = _configuration["Hangfire:DefaultKeyword"] ?? "instagram scraper";
        ViewBag.CronExpression = _configuration["Hangfire:Cron"] ?? "0 */4 * * *";
        ViewBag.CronJobId = CronJobId;
        ViewBag.DashboardRoute = _configuration["Hangfire:DashboardRoute"] ?? "/hangfire";
        return View(runs);
    }

    /// <summary>Enqueues a one-off crawl via Hangfire and redirects to poll its job id.</summary>
    [HttpPost]
    public IActionResult Start(string keyword, bool analyze, bool headless)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return RedirectToAction(nameof(Index));

        var kw = keyword.Trim();
        var jobId = "manual-" + Guid.NewGuid().ToString("N")[..12];
        var maxListings = _configuration.GetValue("Hangfire:MaxListings", 200);
        _backgroundJobs.Enqueue<CrawlJobService>(svc => svc.RunCrawlAsync(jobId, kw, analyze, maxListings, headless));
        return RedirectToAction(nameof(Index), new { jobId });
    }

    /// <summary>Enqueues a scrape of the RapidAPI "Popular APIs" collection.</summary>
    [HttpPost]
    public IActionResult StartPopular(bool headless)
    {
        var jobId = "popular-" + Guid.NewGuid().ToString("N")[..12];
        var maxListings = _configuration.GetValue("Hangfire:MaxListings", 200);
        _backgroundJobs.Enqueue<CrawlJobService>(svc => svc.RunPopularCrawlAsync(jobId, false, maxListings, headless));
        return RedirectToAction(nameof(Index), new { jobId });
    }

    /// <summary>Triggers the scheduled cron job immediately from the UI.</summary>
    [HttpPost]
    public IActionResult RunScheduled()
    {
        RecurringJob.TriggerJob(CronJobId);
        return RedirectToAction(nameof(Index), new { jobId = CronJobId });
    }

    /// <summary>Returns the live crawl job progress as JSON for the page to poll (every 5s).</summary>
    [HttpGet]
    public IActionResult Progress(string? jobId)
        => Json(_coordinator.Snapshot(jobId));

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
        => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
