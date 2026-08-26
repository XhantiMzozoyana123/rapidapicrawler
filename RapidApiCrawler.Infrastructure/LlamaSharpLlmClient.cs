using LLama;
using LLama.Common;
using LLama.Native;
using RapidApiCrawler.Application;

namespace RapidApiCrawler.Infrastructure;

/// <summary>
/// Local LLM analyzer using LLamaSharp (llama.cpp bindings). Runs entirely on the VPS —
/// no API keys or external calls needed. Requires a GGUF model file, configured via
/// LlamaOptions.ModelPath (env var LLAMA_MODEL_PATH or "Llama:ModelPath" in config).
/// Recommended models: Qwen2.5-7B-Instruct, Mistral-7B-Instruct, Llama-3.1-8B-Instruct (Q4/Q5 quantized).
/// </summary>
public class LlamaSharpLlmClient : ILlmAnalyzer
{
    private readonly LlamaOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Lazy<LlamaRuntime?> _runtime;

    private static bool _nativeLoggingConfigured;

    public LlamaSharpLlmClient(LlamaOptions options)
    {
        _options = options;
        ConfigureNativeLogging();
        _runtime = new Lazy<LlamaRuntime?>(() => LoadModel());
    }

    /// <summary>
    /// Must run BEFORE any llama.cpp native library is loaded. Prints backend info to the
    /// console so you can verify CUDA (not CPU) was initialized — LLamaSharp can otherwise
    /// silently fall back to CPU on Linux if the CUDA .so files fail to load.
    /// Watch `nvidia-smi` on the VPS: VRAM usage should jump when the model loads.
    /// </summary>
    private static void ConfigureNativeLogging()
    {
        if (_nativeLoggingConfigured) return;
        _nativeLoggingConfigured = true;
        NativeLibraryConfig.All.WithLogCallback((level, message) =>
            Console.WriteLine($"[llama.{level}] {message.TrimEnd()}"));
    }

    private sealed record LlamaRuntime(LLamaWeights Weights, ModelParams Parameters)
    {
        public StatelessExecutor CreateExecutor() => new(Weights, Parameters);
    }

    private LlamaRuntime? LoadModel()
    {
        if (string.IsNullOrWhiteSpace(_options.ModelPath))
            return null; // no model configured — AnalyzeAsync will throw a clear error

        if (!File.Exists(_options.ModelPath))
            throw new FileNotFoundException(
                $"LLM model file not found: {_options.ModelPath}. " +
                "Download a GGUF model (e.g. from huggingface.co) and set LLAMA_MODEL_PATH.", _options.ModelPath);

                var parameters = new ModelParams(_options.ModelPath)
        {
            ContextSize = (uint)_options.ContextSize,
            GpuLayerCount = _options.GpuLayerCount,
            Threads = _options.ThreadCount > 0 ? _options.ThreadCount : Environment.ProcessorCount,
            FlashAttention = _options.FlashAttention,
        };
        Console.WriteLine($"[llm] Loading '{Path.GetFileName(_options.ModelPath)}' " +
                          $"(context={_options.ContextSize}, gpuLayers={_options.GpuLayerCount}, flashAttn={_options.FlashAttention})...");
        var weights = LLamaWeights.LoadFromFile(parameters);
        Console.WriteLine("[llm] Model loaded. If GpuLayerCount > 0 and CUDA is working, " +
                          "'nvidia-smi' should now show VRAM in use by this process.");
        return new LlamaRuntime(weights, parameters);
    }

    public async Task<string> AnalyzeAsync(string keyword, string combinedContext, CancellationToken ct = default)
    {
        var runtime = _runtime.Value ?? throw new InvalidOperationException(
            "No LLM model configured. Set LLAMA_MODEL_PATH to a GGUF model file to enable AI analysis.");

        // Same gap-analysis prompt as before — model-agnostic plain-text completion.
        var prompt =
            $@"You are a market research analyst specializing in API marketplaces.
A competitor scan of RapidAPI for the keyword ""{keyword}"" found these APIs:
{combinedContext}

Produce a concise competitor gap-analysis report in Markdown with these sections:
1. Market Overview  2. Competitor Landscape (table)  3. Gaps & Underserved Needs
4. Recommended APIs to Build (top 3 ideas, each with target users, key endpoints, differentiation)
5. Risks. Be specific and actionable.

Report:";

        await _lock.WaitAsync(ct); // one inference at a time — the model is a shared resource
        try
        {
            var executor = runtime.CreateExecutor();
            var sb = new System.Text.StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, new InferenceParams
            {
                MaxTokens = _options.MaxTokens,
                AntiPrompts = new[] { "User:", "\n\n\n" },
            }, cancellationToken: ct))
            {
                sb.Append(token);
            }
            return sb.ToString().Trim();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_runtime.IsValueCreated && _runtime.Value is not null)
        {
            try { _runtime.Value.Weights.Dispose(); } catch { /* ignore */ }
        }
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Options for the local LLamaSharp model.</summary>
public record LlamaOptions
{
    /// <summary>Full path to a .gguf model file. Env var: LLAMA_MODEL_PATH</summary>
    public string ModelPath { get; init; } = string.Empty;

    /// <summary>Context window size (tokens). Larger = more competitor context fits in.</summary>
    public int ContextSize { get; init; } = 4096;

    /// <summary>Max tokens the report may generate.</summary>
    public int MaxTokens { get; init; } = 1200;

        /// <summary>Layers offloaded to GPU. 999 = offload as many as VRAM allows (recommended with CUDA12 backend).</summary>
    public int GpuLayerCount { get; init; } = 999;

    /// <summary>CPU threads (0 = auto).</summary>
    public int ThreadCount { get; init; } = 0;

    /// <summary>Flash attention — faster/leaner on CUDA-capable GPUs like the RTX A4000.</summary>
    public bool FlashAttention { get; init; } = true;
}