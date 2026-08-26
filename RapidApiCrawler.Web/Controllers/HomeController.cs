using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;
using RapidApiCrawler.Infrastructure;
using RapidApiCrawler.Web.Models;
using RapidApiCrawler.Web.Services;

namespace RapidApiCrawler.Web.Controllers;

public class HomeController : Controller
{
    private readonly CrawlOrchestrator _orchestrator;
    private readonly ISearchRunRepository _repository;
    private readonly ScraperOptions _scraperOptions;
    private readonly CrawlSessionStore _store;

    public HomeController(
        CrawlOrchestrator orchestrator,
        ISearchRunRepository repository,
        ScraperOptions scraperOptions,
        CrawlSessionStore store)
    {
        _orchestrator = orchestrator;
        _repository = repository;
        _scraperOptions = scraperOptions;
        _store = store;
    }

    public async Task<IActionResult> Index()
    {
        var runs = await _repository.GetRunsAsync();
        ViewBag.Snapshot = _store.Snapshot();
        return View(runs);
    }

    [HttpPost]
    public IActionResult Start(string keyword, bool analyze, bool headless)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return RedirectToAction(nameof(Index));

        // Apply the headless/headed preference.
        _scraperOptions.Headless = headless;

        if (!_store.IsRunning)
        {
            _store.Start(keyword.Trim());
            var runAnalyze = analyze;
            _ = Task.Run(async () =>
            {
                try
                {
                    var ct = CancellationToken.None;
                    // Subscribe so progress is pushed into the store while the crawl runs.
                    var progress = new EventHandler<ProgressEventArgs>((_, e) => _store.Append(e.Message));
                    _orchestrator.Progress += progress;
                    try
                    {
                        var run = await _orchestrator.RunAsync(keyword.Trim(), runAnalyze, ct, maxListings: 200);
                        _store.Complete(run.Id, run.ListingsFound, run.PagesCrawled);
                    }
                    finally
                    {
                        _orchestrator.Progress -= progress;
                    }
                }
                catch (Exception ex)
                {
                    _store.Fail(ex.Message);
                }
            });
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Returns the current crawl progress as JSON for the page to poll.</summary>
    [HttpGet]
    public IActionResult Progress()
        => Json(_store.Snapshot());

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
        => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
