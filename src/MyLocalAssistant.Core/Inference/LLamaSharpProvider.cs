using System.Runtime.CompilerServices;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyLocalAssistant.Core.Inference;

public sealed class LLamaSharpProvider : ILlmProvider
{
    private readonly ILogger<LLamaSharpProvider> _logger;
    private LLamaWeights? _weights;
    private ModelParams? _params;
    private string? _modelId;

    public LLamaSharpProvider(ILogger<LLamaSharpProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<LLamaSharpProvider>.Instance;
    }

    public string? LoadedModelId => _modelId;

    public string Backend => BackendSelector.SelectedBackend;

    public async Task LoadAsync(string modelPath, string modelId, int contextSize, CancellationToken ct = default)
    {
        BackendSelector.Configure(_logger);
        await UnloadAsync().ConfigureAwait(false);

        _logger.LogInformation("Loading model {Id} from {Path} (ctx={Ctx})", modelId, modelPath, contextSize);
        _params = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = int.MaxValue, // offload as much as possible; falls back to CPU below
        };
        try
        {
            _weights = await LLamaWeights.LoadFromFileAsync(_params, ct).ConfigureAwait(false);
            // On integrated GPUs (e.g. Intel HD 620 with Vulkan) the weights may load
            // successfully from file but executor creation or the first forward pass will
            // fail when llama.cpp tries to move KV-cache / compute buffers to the tiny
            // shared VRAM.  Probe with a zero-token executor now, while we can still
            // cheaply fall back, rather than failing mid-inference later.
            if (_params.GpuLayerCount != 0)
                TryProbeExecutor(_weights, _params);
        }
        catch (Exception ex) when (_params.GpuLayerCount > 0)
        {
            // GPU path failed — either during file load or during the probe above.
            // Reload with CPU-only so the model still works on low-end hardware.
            _logger.LogWarning(ex, "GPU load/probe failed for {Id} — retrying with CPU-only.", modelId);
            _weights?.Dispose();
            _weights = null;
            _params = new ModelParams(modelPath) { ContextSize = (uint)contextSize, GpuLayerCount = 0 };
            _weights = await LLamaWeights.LoadFromFileAsync(_params, ct).ConfigureAwait(false);
        }
        _modelId = modelId;
        _logger.LogInformation("Model {Id} loaded (gpuLayers={Gpu}).", modelId,
            _params.GpuLayerCount == 0 ? "CPU-only" : _params.GpuLayerCount.ToString());
    }

    public IAsyncEnumerable<string> GenerateAsync(
        string prompt,
        int maxTokens,
        CancellationToken ct = default)
        => GenerateAsync(prompt, maxTokens, Array.Empty<string>(), ct);

    public async IAsyncEnumerable<string> GenerateAsync(
        string prompt,
        int maxTokens,
        IReadOnlyList<string> extraStops,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_weights is null || _params is null)
            throw new InvalidOperationException("No model is loaded. Call LoadAsync first.");

        var executor = new StatelessExecutor(_weights, _params);
        var antis = new List<string> { "\nUser:", "\nuser:" };
        if (extraStops is { Count: > 0 })
            foreach (var s in extraStops)
                if (!string.IsNullOrEmpty(s)) antis.Add(s);
        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            // Stop the moment the model tries to start a new "User:" turn (matches the
            // simple plain-text framing emitted by ChatService.BuildPrompt) or hits one
            // of the caller-supplied tool-loop stops (e.g. </tool_call>).
            AntiPrompts = antis,
            SamplingPipeline = new DefaultSamplingPipeline(),
        };

        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
        {
            yield return token;
        }
    }

    public Task UnloadAsync()
    {
        if (_weights is not null)
        {
            _logger.LogInformation("Unloading model {Id}", _modelId);
            _weights.Dispose();
            _weights = null;
            _params = null;
            _modelId = null;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await UnloadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a <see cref="StatelessExecutor"/> against the given weights and immediately
    /// disposes it. This forces llama.cpp to allocate KV-cache and compute buffers, which
    /// is where integrated-GPU (Vulkan) failures typically surface — before the first real
    /// inference call. Throws if executor creation fails.
    /// </summary>
    private static void TryProbeExecutor(LLamaWeights weights, ModelParams p)
    {
        var exec = new StatelessExecutor(weights, p);
        // StatelessExecutor itself is not IDisposable in LLamaSharp — it shares the
        // weights reference.  Creating it is enough to trigger the allocation check.
        _ = exec;
    }
}
