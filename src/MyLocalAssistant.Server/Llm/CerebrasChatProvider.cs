using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Configuration;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Chat provider for the Cerebras Cloud API (OpenAI-compatible /v1/chat/completions).
/// Cerebras provides extremely fast LPU-based inference with a generous free tier.
/// Free tier: ~20 req/min — no credit card required.
/// Models: llama3.1-8b, llama3.1-70b, llama-4-scout-17b-16e-instruct, qwen-3-32b.
/// Get a free API key at: https://cloud.cerebras.ai
/// </summary>
public sealed class CerebrasChatProvider : IChatProvider
{
    private const string BaseUrl = "https://api.cerebras.ai/v1";

    private readonly ServerSettings _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CerebrasChatProvider> _log;
    private readonly CloudCircuitBreaker _circuit;

    public CerebrasChatProvider(ServerSettings settings, IHttpClientFactory httpFactory, ILogger<CerebrasChatProvider> log)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _log = log;
        _circuit = new CloudCircuitBreaker("cerebras", log);
    }

    public ModelSource Source => ModelSource.Cerebras;

    public bool IsReady(CatalogEntry entry) => _settings.IsCerebrasConfigured;

    public string? UnavailableReason(CatalogEntry entry) =>
        IsReady(entry) ? null : "Cerebras API key is not configured. Open Server Settings → Cloud keys (global admin only).";

    public Task LoadAsync(CatalogEntry entry, string? localFilePath, CancellationToken ct)
    {
        if (!_settings.IsCerebrasConfigured)
            throw new InvalidOperationException("Cerebras API key is not configured.");
        return Task.CompletedTask;
    }

    public Task UnloadAsync() => Task.CompletedTask;

    public async IAsyncEnumerable<string> GenerateAsync(
        CatalogEntry entry, string prompt, int maxTokens, IReadOnlyList<string> stops,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var token in _circuit.ExecuteAsync(() => GenerateCoreAsync(entry, prompt, maxTokens, stops, ct), ct).ConfigureAwait(false))
            yield return token;
    }

    private async IAsyncEnumerable<string> GenerateCoreAsync(
        CatalogEntry entry, string prompt, int maxTokens, IReadOnlyList<string> stops,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = _settings.GetCerebrasApiKey()
            ?? throw new InvalidOperationException("Cerebras API key is not configured.");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var modelName = string.IsNullOrWhiteSpace(entry.RemoteModel) ? entry.Id : entry.RemoteModel;
        var stopArr = stops is { Count: > 0 } ? stops.Take(4).ToArray() : null;
        var body = new
        {
            model = modelName,
            stream = true,
            max_tokens = maxTokens,
            messages = new object[] { new { role = "user", content = prompt } },
            stop = stopArr,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Cerebras returned {(int)resp.StatusCode}: {Truncate(err, 400)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.Substring(5).Trim();
            if (payload == "[DONE]") yield break;
            if (payload.Length == 0) continue;

            string? token = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        token = content.GetString();
                    }
                }
            }
            catch (JsonException ex)
            {
                _log.LogDebug(ex, "Cerebras: ignoring malformed SSE payload ({Snippet})", Truncate(payload, 120));
                continue;
            }

            if (!string.IsNullOrEmpty(token)) yield return token;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
