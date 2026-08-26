using System.Text;
using RapidApiCrawler.Application;
using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Writes the scraped database tables out as CSV files (one per table):
/// SearchRuns.csv, ApiListings.csv, CrawledPages.csv, AnalysisReports.csv.
/// Values are RFC-4180 escaped so commas, quotes and newlines (e.g. in HTML) survive.
/// SearchRuns.csv, ApiListings.csv, CrawledPages.csv, AnalysisReports.csv.
/// Values are RFC-4180 escaped so commas, quotes and newlines (e.g. in HTML) survive.
/// </summary>
public class CsvExporter : ICsvExporter
{
    private readonly ISearchRunRepository _repository;

    public CsvExporter(ISearchRunRepository repository) => _repository = repository;

    public async Task<List<string>> ExportAllToCsvAsync(string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();

        foreach (var table in await _repository.GetTableNamesAsync())
        {
            ct.ThrowIfCancellationRequested();
            var result = await _repository.QueryTableAsync(table, limit: null);
            if (result.Columns.Length == 0)
                continue; // empty table — nothing to export

            var path = Path.Combine(directory, $"{table}.csv");
            await File.WriteAllTextAsync(path, BuildCsv(result), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            written.Add(path);
        }

        return written;
    }

    public async Task<string?> ExportTableToCsvAsync(string tableName, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);

        var valid = await _repository.GetTableNamesAsync();
        var safe = valid.FirstOrDefault(t => t.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        if (safe is null)
            return null;

        var result = await _repository.QueryTableAsync(safe, limit: null);
        if (result.Columns.Length == 0)
            return null; // empty table

        var path = Path.Combine(directory, $"{safe}.csv");
        await File.WriteAllTextAsync(path, BuildCsv(result), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>Builds a single CSV string (header row + data rows) with proper escaping.</summary>
    private static string BuildCsv(TableResult result)
    {
        var sb = new StringBuilder();

        // Header row.
        sb.AppendLine(string.Join(',', result.Columns.Select(Escape)));
        foreach (var row in result.Rows)
            sb.AppendLine(string.Join(',', row.Select(cell => Escape(cell?.ToString() ?? ""))));

        return sb.ToString();
    }

    /// <summary>Escapes a field per RFC 4180: quote if it contains a delimiter; double inner quotes.</summary>
    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}