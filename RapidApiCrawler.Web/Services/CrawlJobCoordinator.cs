namespace RapidApiCrawler.Web.Services;

/// <summary>Immutable snapshot of one crawl job, as returned to the UI for polling.</summary>
public sealed record CrawlJobSnapshot(
    bool Exists,
    string JobId,
    string Status,
    bool IsRunning,
    string Keyword,
    int? RunId,
    int ListingsFound,
    int PagesCrawled,
    string CurrentScraping,
    string? Error,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    IReadOnlyList<string> Messages);

/// <summary>
/// Thread-safe, in-process registry of crawl job progress, keyed by the Hangfire job id.
/// The crawl runs on a Hangfire worker; controllers/views poll <see cref="Snapshot"/> to
/// show live status and the exact listing/page currently being scraped.
/// </summary>
public class CrawlJobCoordinator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CrawlJobState> _jobs = new();

    public bool HasRunningJobs
    {
        get
        {
            lock (_lock)
                return _jobs.Values.Any(j => j.IsRunning);
        }
    }

    /// <summary>
    /// Atomically claims a job for <paramref name="jobId"/>. Returns <c>false</c> (and does not
    /// start anything) if any crawl is already running, guarding the shared Playwright browser.
    /// </summary>
    public bool TryBegin(string jobId, string keyword)
    {
        lock (_lock)
        {
            if (_jobs.Values.Any(j => j.IsRunning))
                return false;

            _jobs[jobId] = new CrawlJobState
            {
                JobId = jobId,
                Keyword = keyword,
                Status = "Running",
                IsRunning = true,
                StartedUtc = DateTime.UtcNow,
                CurrentScraping = $"Starting search for '{keyword}'..."
            };
            _jobs[jobId].Messages.Add($"Starting crawl for '{keyword}'...");
            return true;
        }
    }

    public bool IsRunning(string jobId)
    {
        lock (_lock)
            return _jobs.TryGetValue(jobId, out var j) && j.IsRunning;
    }

    public void Append(string jobId, string message)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var j))
            {
                j.Messages.Add(message);
                // Keep "CurrentScraping" in sync with the latest progress line so the
                // UI shows exactly what is being scraped right now.
                j.CurrentScraping = message;
            }
        }
    }

    public void Complete(string jobId, int runId, int listings, int pages)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var j))
            {
                j.RunId = runId;
                j.ListingsFound = listings;
                j.PagesCrawled = pages;
                j.Status = "Completed";
                j.IsRunning = false;
                j.CompletedUtc = DateTime.UtcNow;
                j.Messages.Add($"Done: {listings} listings, {pages} pages captured (run #{runId}).");
            }
        }
    }

    public void Fail(string jobId, string error)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var j))
            {
                j.Status = "Failed";
                j.IsRunning = false;
                j.CompletedUtc = DateTime.UtcNow;
                j.Error = error;
                j.CurrentScraping = "Crawl failed.";
                j.Messages.Add("FAILED: " + error);
            }
        }
    }

    public CrawlJobSnapshot Snapshot(string? jobId)
    {
        lock (_lock)
        {
            CrawlJobState? state = null;
            if (!string.IsNullOrWhiteSpace(jobId) && _jobs.TryGetValue(jobId, out var byId))
                state = byId;

            state ??= _jobs.Values.OrderByDescending(j => j.StartedUtc).FirstOrDefault();

            if (state is null)
                return new CrawlJobSnapshot(false, string.Empty, "Idle", false, string.Empty, null, 0, 0,
                    "No crawl has run in this process yet.", null, default, null, Array.Empty<string>());

            return new CrawlJobSnapshot(
                true, state.JobId, state.Status, state.IsRunning, state.Keyword, state.RunId,
                state.ListingsFound, state.PagesCrawled, state.CurrentScraping, state.Error,
                state.StartedUtc, state.CompletedUtc, state.Messages.ToArray());
        }
    }

    public IReadOnlyList<CrawlJobSnapshot> All()
    {
        lock (_lock)
        {
            return _jobs.Values
                .OrderByDescending(j => j.StartedUtc)
                .Select(j => new CrawlJobSnapshot(
                    true, j.JobId, j.Status, j.IsRunning, j.Keyword, j.RunId,
                    j.ListingsFound, j.PagesCrawled, j.CurrentScraping, j.Error,
                    j.StartedUtc, j.CompletedUtc, j.Messages.ToArray()))
                .ToList();
        }
    }

    private sealed class CrawlJobState
    {
        public string JobId { get; init; } = string.Empty;
        public string Status { get; set; } = "Queued";
        public bool IsRunning { get; set; } = true;
        public string Keyword { get; set; } = string.Empty;
        public int? RunId { get; set; }
        public int ListingsFound { get; set; }
        public int PagesCrawled { get; set; }
        public string CurrentScraping { get; set; } = string.Empty;
        public string? Error { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedUtc { get; set; }
        public List<string> Messages { get; } = new();
    }
}