namespace RapidApiCrawler.Web.Services;

/// <summary>A snapshot of the current crawl session for UI polling.</summary>
public record CrawlSnapshot(
    string Status,
    bool IsRunning,
    string Keyword,
    int? RunId,
    int ListingsFound,
    int PagesCrawled,
    IReadOnlyList<string> Messages);

/// <summary>
/// Thread-safe, singleton in-memory store that tracks the in-progress crawl.
/// The crawl runs on a background task; controller/views poll this for progress.
/// </summary>
public class CrawlSessionStore
{
    private readonly object _lock = new();
    private readonly List<string> _messages = new();

    public string Status { get; private set; } = "Idle";
    public bool IsRunning { get; private set; }
    public string Keyword { get; private set; } = string.Empty;
    public int? RunId { get; private set; }
    public int ListingsFound { get; private set; }
    public int PagesCrawled { get; private set; }

    public void Start(string keyword)
    {
        lock (_lock)
        {
            Keyword = keyword;
            IsRunning = true;
            Status = "Running";
            RunId = null;
            ListingsFound = 0;
            PagesCrawled = 0;
            _messages.Clear();
            _messages.Add($"Starting search for '{keyword}'...");
        }
    }

    public void Append(string message)
    {
        lock (_lock) _messages.Add(message);
    }

    public void Complete(int runId, int listings, int pages)
    {
        lock (_lock)
        {
            IsRunning = false;
            Status = "Completed";
            RunId = runId;
            ListingsFound = listings;
            PagesCrawled = pages;
            _messages.Add($"Done: {listings} listings, {pages} pages captured.");
        }
    }

    public void Fail(string message)
    {
        lock (_lock)
        {
            IsRunning = false;
            Status = "Failed";
            _messages.Add("FAILED: " + message);
        }
    }

    public CrawlSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new CrawlSnapshot(Status, IsRunning, Keyword, RunId, ListingsFound, PagesCrawled,
                _messages.ToArray());
        }
    }
}