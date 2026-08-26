using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;

namespace RapidApiCrawler.Web.Controllers;

public class DatabaseController : Controller
{
    private readonly ISearchRunRepository _repository;

    public DatabaseController(ISearchRunRepository repository) => _repository = repository;

    public async Task<IActionResult> Index(string? table)
    {
        var tables = await _repository.GetTableNamesAsync();
        ViewBag.Tables = tables;

        RapidApiCrawler.Domain.TableResult? result = null;
        if (!string.IsNullOrWhiteSpace(table) && tables.Contains(table, StringComparer.OrdinalIgnoreCase))
        {
            result = await _repository.QueryTableAsync(table, 300);
            ViewBag.SelectedTable = tables.First(t => t.Equals(table, StringComparison.OrdinalIgnoreCase));
        }
        return View(result ?? new RapidApiCrawler.Domain.TableResult(Array.Empty<string>(), new List<object?[]>()));
    }

    /// <summary>Deletes every row of a single table, then returns to the Database page.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearTable(string table)
    {
        var deleted = await _repository.ClearTableAsync(table);
        TempData["ExportMessage"] = $"Deleted {deleted} row(s) from '{table}'.";
        return RedirectToAction(nameof(Index), new { table });
    }

    /// <summary>Deletes every data record from all crawler tables.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAll()
    {
        var deleted = await _repository.ClearAllDataAsync();
        TempData["ExportMessage"] = $"Deleted {deleted} record(s) from all tables.";
        return RedirectToAction(nameof(Index));
    }
}