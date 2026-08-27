using RapidApiCrawler.Domain;

namespace RapidApiCrawler.Application;
public interface IRapidApiClient
{
    /// <summary>Opens the search page and collects all listing links across pages until no "Next Page" exists.</summary>
    IAsyncEnumerable<ApiListing> SearchListingsAsync(string keyword, int runId, CancellationToken ct = default);

    /// <summary>Crawls the RapidAPI "Popular APIs" collection and yields every API listing found.</summary>
    IAsyncEnumerable<ApiListing> PopularListingsAsync(int runId, CancellationToken ct = default);

    /// <summary>
    /// Opens a listing in a new tab, clicks the Discussions tab and captures every
    /// discussions-list page (following "next" pagination until exhausted), then closes the tab.
    /// Returns the captured discussions pages with their HTML.
    /// </summary>
    Task<List<CrawledPage>> CaptureListingAsync(ApiListing listing, CancellationToken ct = default);
}

public interface ILlmAnalyzer
{
    Task<string> AnalyzeAsync(string keyword, string combinedContext, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="AnalyzeAsync"/> but reports incremental progress (e.g. token
    /// counts streamed from the LLM backend) through <paramref name="progress"/>.
    /// <param name="progress" />.
    /// </summary>
    Task<string> AnalyzeAsync(string keyword, string combinedContext, IProgress<string> progress, CancellationToken ct = default);

    /// <summary>
    /// Low-level single-prompt completion. Sends an arbitrary prompt to the LLM and returns
    /// the generated text. The chunked-analysis pipeline calls this repeatedly with smaller
    /// prompts (one per listing-batch / report-section) instead of one large
    /// <see cref="AnalyzeAsync"/> call, dramatically reducing per-request latency.
    /// </summary>
    Task<string> CompleteAsync(string prompt, int maxTokens, IProgress<string> progress, CancellationToken ct = default);
}

public interface ISearchRunRepository
{
    Task<int> CreateRunAsync(SearchRun run);
    Task UpdateRunAsync(SearchRun run);
    Task AddListingAsync(ApiListing listing);
    Task AddPageAsync(CrawledPage page);
    Task AddReportAsync(AnalysisReport report);
    Task<List<ApiListing>> GetListingsAsync(int runId);

    /// <summary>
    /// Returns all captured discussion/comment pages ('Discussions' PageType) for every
    /// listing belonging to <paramref name="runId"/>, ordered by listing then capture order.
    /// Used by the gap-analysis pipeline so customer feedback on each API's RapidAPI page
    /// can inform the report.
    /// </summary>
    Task<List<CrawledPage>> GetDiscussionPagesAsync(int runId);
    Task<List<SearchRun>> GetRunsAsync();
    Task<string?> GetLatestReportAsync(int runId);
    Task<int> CountPagesAsync(int runId);
    Task<List<string>> GetTableNamesAsync();
    Task<TableResult> QueryTableAsync(string tableName, int? limit = 200);

    /// <summary>Deletes every row from <paramref name="tableName"/> and returns the number of affected rows.</summary>
    Task<int> ClearTableAsync(string tableName);

    /// <summary>Deletes every data record from all crawler tables and returns total rows removed.</summary>
    Task<int> ClearAllDataAsync();
}

/// <summary>Writes the scraped database tables out as CSV files.</summary>
public interface ICsvExporter
{
    /// <summary>Writes one CSV per table into <paramref name="directory"/> and returns the created file paths.</summary>
    Task<List<string>> ExportAllToCsvAsync(string directory, CancellationToken ct = default);

    /// <summary>Writes a single table to a CSV file in <paramref name="directory"/> and returns its full path, or null if empty/missing.</summary>
    Task<string?> ExportTableToCsvAsync(string tableName, string directory, CancellationToken ct = default);
}