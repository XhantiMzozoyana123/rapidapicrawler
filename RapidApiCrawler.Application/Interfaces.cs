using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Application;
public interface IRapidApiClient
{
    /// <summary>Opens the search page and collects all listing links across pages until no "Next Page" exists.</summary>
    IAsyncEnumerable<ApiListing> SearchListingsAsync(string keyword, int runId, CancellationToken ct = default);

    /// <summary>
    /// Opens a listing in a new tab, captures the playground page HTML, the API home page HTML
    /// (breadcrumb link) and the Discussions tab HTML, then closes the tab.
    /// Returns the captured page types with their HTML.
    /// </summary>
    Task<List<CrawledPage>> CaptureListingAsync(ApiListing listing, CancellationToken ct = default);
}

public interface ILlmAnalyzer
{
    Task<string> AnalyzeAsync(string keyword, string combinedContext, CancellationToken ct = default);
}

public interface ISearchRunRepository
{
    Task<int> CreateRunAsync(SearchRun run);
    Task UpdateRunAsync(SearchRun run);
    Task AddListingAsync(ApiListing listing);
    Task AddPageAsync(CrawledPage page);
    Task AddReportAsync(AnalysisReport report);
    Task<List<ApiListing>> GetListingsAsync(int runId);
    Task<List<SearchRun>> GetRunsAsync();
    Task<string?> GetLatestReportAsync(int runId);
    Task<int> CountPagesAsync(int runId);
    Task<List<string>> GetTableNamesAsync();
    Task<TableResult> QueryTableAsync(string tableName, int? limit = 200);
}

/// <summary>Writes the scraped database tables out as CSV files.</summary>
public interface ICsvExporter
{
    /// <summary>Writes one CSV per table into <paramref name="directory"/> and returns the created file paths.</summary>
    Task<List<string>> ExportAllToCsvAsync(string directory, CancellationToken ct = default);

    /// <summary>Writes a single table to a CSV file in <paramref name="directory"/> and returns its full path, or null if empty/missing.</summary>
    Task<string?> ExportTableToCsvAsync(string tableName, string directory, CancellationToken ct = default);
}