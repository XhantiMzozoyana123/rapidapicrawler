using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Web.Controllers;

public class ListingsController : Controller
{
    private readonly ISearchRunRepository _repository;

    public ListingsController(ISearchRunRepository repository) => _repository = repository;

    public async Task<IActionResult> Index(int? runId)
    {
        var runs = await _repository.GetRunsAsync();
        var allListings = new List<ApiListing>();
        foreach (var run in runs)
            allListings.AddRange(await _repository.GetListingsAsync(run.Id));

        ViewBag.Runs = runs;
        ViewBag.SelectedRunId = runId;
        var filtered = runId.HasValue
            ? allListings.Where(l => l.SearchRunId == runId.Value).ToList()
            : allListings;
        return View(filtered);
    }
}