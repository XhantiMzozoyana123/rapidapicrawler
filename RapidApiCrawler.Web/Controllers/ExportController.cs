using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using RapidApiCrawler.Application;

namespace RapidApiCrawler.Web.Controllers;

public class ExportController : Controller
{
    private readonly ISearchRunRepository _repository;
    private readonly ICsvExporter _exporter;

    public ExportController(ISearchRunRepository repository, ICsvExporter exporter)
    {
        _repository = repository;
        _exporter = exporter;
    }

    /// <summary>Downloads every scraped table as a single ZIP of CSVs.</summary>
    public async Task<IActionResult> All()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rapidapi-exports-" + Guid.NewGuid().ToString("N"));
        var files = await _exporter.ExportAllToCsvAsync(dir);
        if (files.Count == 0)
        {
            TempData["ExportMessage"] = "Nothing to export yet — run a crawl first.";
            return RedirectToAction(null, "Home");
        }

        var zipPath = Path.Combine(Path.GetTempPath(), $"rapidapi-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in files)
                zip.CreateEntryFromFile(file, Path.GetFileName(file));
        }

        Directory.Delete(dir, recursive: true);
        return File(System.IO.File.ReadAllBytes(zipPath), "application/zip", Path.GetFileName(zipPath));
    }

    /// <summary>Downloads a single table as a CSV file.</summary>
    public async Task<IActionResult> Table(string table)
    {
        var tables = await _repository.GetTableNamesAsync();
        var safe = tables.FirstOrDefault(t => t.Equals(table, StringComparison.OrdinalIgnoreCase));
        if (safe is null)
        {
            TempData["ExportMessage"] = $"Unknown table '{table}'.";
            return RedirectToAction(nameof(Index), "Database");
        }

        var dir = Path.Combine(Path.GetTempPath(), "rapidapi-exports-" + Guid.NewGuid().ToString("N"));
        var file = await _exporter.ExportTableToCsvAsync(safe, dir);
        if (file is null)
        {
            Directory.Delete(dir, recursive: true);
            TempData["ExportMessage"] = $"Table '{safe}' has no rows to export.";
            return RedirectToAction(nameof(Index), "Database");
        }
        var bytes = await System.IO.File.ReadAllBytesAsync(file);
        Directory.Delete(dir, recursive: true);
        return File(bytes, "text/csv", $"{safe}.csv");
    }
}