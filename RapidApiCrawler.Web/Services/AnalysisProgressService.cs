using System.Collections.Concurrent;
using RapidApiCrawler.Application;

namespace RapidApiCrawler.Web.Services;

/// <summary>Snapshot of one run's AI gap-analysis progress, serialised to the UI.</summary>
public sealed class AnalysisProgressState
{
    public string Status { get; set; } = "running";     // running | completed | failed
    public double Percent { get; set; }
    public int TotalRequests { get; set; }
    public int CompletedRequests { get; set; }
    public double CurrentStepPercent { get; set; }      // 0-100 within the active LLM request
    public string CurrentStep { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// In-memory store of AI gap-analysis progress, keyed by crawl-run id. Self-subscribes
/// to the orchestrator's structured <see cref="AnalysisProgressEventArgs"/> events so
/// every analysis (cron crawl or the Report page's Generate button) is tracked
/// automatically. The Report page polls it via /Report/AnalysisProgress and renders a
/// live percentage + step description so users never have to guess what the LLM is doing.
/// </summary>
public sealed class AnalysisProgressService
{
    private readonly ConcurrentDictionary<int, AnalysisProgressState> _states = new();

    public AnalysisProgressService(CrawlOrchestrator orchestrator)
    {
        // Handler stays synchronous — event invocation is synchronous and state updates are atomic.
        orchestrator.AnalysisProgress += (_, e) =>
        {
            var state = _states.GetOrAdd(e.RunId, _ => new AnalysisProgressState());
            lock (state)
            {
                var total = Math.Max(e.TotalRequests, 1);
                var stepFraction = e.CurrentRequestMaxTokens > 0
                    ? Math.Min((double)e.CurrentRequestTokens / e.CurrentRequestMaxTokens, 0.9)
                    : 0;

                state.Status = "running";
                state.TotalRequests = total;
                state.CompletedRequests = Math.Min(e.CompletedRequests, total);
                state.CurrentStepPercent = Math.Round(100 * stepFraction, 1);
                state.Percent = Math.Min(Math.Round(
                    100.0 * (e.CompletedRequests + stepFraction) / total, 1), 99.5);
                state.CurrentStep = e.CurrentStep;
                state.Message = $"LLM request {Math.Min(e.CompletedRequests + 1, total)} of {total}: {e.CurrentStep}";
                state.UpdatedUtc = DateTime.UtcNow;
            }
        };
    }

    /// <summary>Marks a run's analysis as started (before any LLM request fires).</summary>
    public void Start(int runId)
    {
        var state = _states.GetOrAdd(runId, _ => new AnalysisProgressState());
        lock (state)
        {
            state.Status = "running";
            state.Percent = 0;
            state.Message = "Starting AI gap-analysis...";
            state.CurrentStep = "Preparing";
            state.UpdatedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Gets a thread-safe snapshot, or null when nothing has been tracked for this run.</summary>
    public AnalysisProgressState? Get(int runId)
    {
        if (!_states.TryGetValue(runId, out var state)) return null;
        lock (state)
        {
            return new AnalysisProgressState
            {
                Status = state.Status,
                Percent = state.Percent,
                TotalRequests = state.TotalRequests,
                CompletedRequests = state.CompletedRequests,
                CurrentStepPercent = state.CurrentStepPercent,
                CurrentStep = state.CurrentStep,
                Message = state.Message,
                UpdatedUtc = state.UpdatedUtc,
            };
        }
    }

    /// <summary>Finalises tracking for a successful analysis (100% + "Report ready").</summary>
    public void MarkCompleted(int runId)
    {
        var state = _states.GetOrAdd(runId, _ => new AnalysisProgressState());
        lock (state)
        {
            state.Status = "completed";
            state.Percent = 100;
            state.Message = "Report ready.";
            state.UpdatedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Records an analysis failure so the UI can show an error instead of spinning forever.</summary>
    public void MarkFailed(int runId, string error)
    {
        var state = _states.GetOrAdd(runId, _ => new AnalysisProgressState());
        lock (state)
        {
            state.Status = "failed";
            state.Message = $"Analysis failed: {error}";
            state.UpdatedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>True when this run currently has an in-flight analysis.</summary>
    public bool IsRunning(int runId)
    {
        if (!_states.TryGetValue(runId, out var state)) return false;
        lock (state)
        {
            return state.Status == "running" &&
                   DateTime.UtcNow - state.UpdatedUtc < TimeSpan.FromMinutes(30);
        }
    }
}
