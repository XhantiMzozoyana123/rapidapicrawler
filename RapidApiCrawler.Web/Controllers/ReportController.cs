using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;
using RapidApiCrawler.Web.Models;

namespace RapidApiCrawler.Web.Controllers;

public class ReportController : Controller
{
    private readonly ISearchRunRepository _repository;

    public ReportController(ISearchRunRepository repository) => _repository = repository;

    public async Task<IActionResult> Index(int? runId)
    {
        var runs = await _repository.GetRunsAsync();
        ViewBag.Runs = runs;

        var latest = runId ?? runs.OrderByDescending(r => r.Id).FirstOrDefault()?.Id;
        var report = latest.HasValue ? await _repository.GetLatestReportAsync(latest.Value) : null;

        return View(new ReportViewModel(latest, report));
    }
}