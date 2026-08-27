using System.Text;
using System.Text.Json;
using RapidApiCrawler.Application;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// AI analyzer backed by a remote Ollama server (e.g. running on the VPS host).
/// Talks plain HTTP to /api/generate — no local model loading, no GGUF files, no native libs.
/// Configuration: OLLAMA_URL (default http://127.0.0.1:11434) + OLLAMA_MODEL (default llama3),
/// both overridable per environment. Reuses a single shared HttpClient with a long timeout
/// because inference of the larger report sections can take several minutes.
///
/// Supports chunked analysis: <see cref="CompleteAsync"/> lets the CrawlOrchestrator send
/// multiple smaller prompts (one per listing-batch / report-section) instead of one giant
/// prompt, dramatically reducing per-request latency on resource-constrained VPS hosts.
/// </summary>
public class OllamaLlmClient : ILlmAnalyzer
{
    private static readonly HttpClient Http = CreateSharedClient();

    private readonly string _url;
    private readonly string _model;

    public OllamaLlmClient()
    {
        var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_URL")
                    ?? "http://127.0.0.1:11434";
        _url = baseUrl.TrimEnd('/').EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase)
            ? baseUrl.TrimEnd('/')
            : baseUrl.TrimEnd('/') + "/api/generate";
        _model = Environment.GetEnvironmentVariable("OLLAMA_MODEL")
        ?? "huihui_ai/Qwen3.8-abliterated:latest";
    }

    private static HttpClient CreateSharedClient()
    {
        // Long timeout: generating even a 600-token section can take minutes on a small model.
        return new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
        })
        {
            Timeout = TimeSpan.FromMinutes(20),
        };
    }

    /// <summary>
    /// Low-level single-prompt completion via the Ollama HTTP API. Used by the
    /// chunked analysis pipeline to make multiple smaller chained requests.
    ///
    /// Thinking-mode handling: the configured model is a Qwen3-family reasoning model
    /// that would spend most of each small token budget emitting &lt;think&gt; blocks
    /// instead of the actual answer. We therefore (1) ask Ollama not to think via the
    /// "think": false request option, and (2) soft-switch it off by appending /no_think
    /// to every prompt — older/newer Ollama versions or non-Qwen models simply ignore
    /// both. Any residual thinking block is stripped from the output as a safety net.
    /// </summary>
    public async Task<string> CompleteAsync(string prompt, int maxTokens, IProgress<string> progress, CancellationToken ct = default)
    {
        // Qwen3 soft-switch: appending /no_think makes the model skip thinking even
        // if the Ollama server ignores the "think": false option (older versions).
        var effectivePrompt = Environment.GetEnvironmentVariable("OLLAMA_DISABLE_THINK") == "false"
            ? prompt
            : prompt.TrimEnd() + "\n\n/no_think";

        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            prompt = effectivePrompt,
            stream = true,
            think = false,
            options = new { num_predict = maxTokens }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await Http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var sb = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        const int reportEveryTokens = 100;
        var tokens = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var chunk = JsonDocument.Parse(line);
            var root = chunk.RootElement;
            sb.Append(root.GetProperty("response").GetString());

            tokens++;
            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                var evalCount = root.TryGetProperty("eval_count", out var ec) ? ec.GetInt32() : tokens;
                progress.Report($"Ollama finished: generated {evalCount} tokens for '{_model}'.");
                break;
            }
            if (tokens % reportEveryTokens == 0)
                progress.Report($"Generating... {tokens} tokens.");
        }

        return StripThinkingBlock(sb.ToString()).Trim();
    }

    /// <summary>
    /// Safety net: removes any residual &lt;think&gt;...&lt;/think&gt; block (and an
    /// unterminated trailing one) from a Qwen3 response so reports never contain
    /// raw reasoning text.
    /// </summary>
    private static string StripThinkingBlock(string text)
    {
        while (true)
        {
            var open = text.IndexOf("<think>", StringComparison.Ordinal);
            if (open < 0) break;
            var close = text.IndexOf("</think>", open + 7, StringComparison.Ordinal);
            text = close >= 0
                ? text.Remove(open, close - open + 8).TrimStart()
                : text[..open]; // unterminated thinking — drop everything after <think>
        }
        return text;
    }

    public Task<string> AnalyzeAsync(string keyword, string combinedContext, CancellationToken ct = default)
        => AnalyzeAsync(keyword, combinedContext, NullProgress.Instance, ct);

    public async Task<string> AnalyzeAsync(string keyword, string combinedContext, IProgress<string> progress, CancellationToken ct = default)
    {
        var prompt =
            $@"You are a market research analyst specializing in API marketplaces.
A competitor scan of RapidAPI for the keyword ""{keyword}"" found these APIs:
{combinedContext}

Produce a concise competitor gap-analysis report in Markdown with these sections:
1. Market Overview  2. Competitor Landscape (table)  3. Gaps & Underserved Needs
4. Recommended APIs to Build (top 3 ideas, each with target users, key endpoints, differentiation)
5. Risks. Be specific and actionable.

Report:";

        return await CompleteAsync(prompt, 1200, progress, ct);
    }

    /// <summary>No-op progress adapter used by the parameterless overload.</summary>
    private sealed class NullProgress : IProgress<string>
    {
        public static readonly NullProgress Instance = new();
        public void Report(string value) { }
    }
}
