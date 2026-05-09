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
/// Chat provider for the Google Gemini API via its OpenAI-compatible endpoint.
/// Free tier: 15 req/min, 1 500 req/day — no credit card required.
/// Models: gemini-2.0-flash, gemini-2.0-flash-lite, gemini-1.5-flash-8b.
/// API keys obtained from https://aistudio.google.com/apikey.
/// </summary>
public sealed class GeminiChatProvider : IChatProvider
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";

    private readonly ServerSettings _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GeminiChatProvider> _log;
    private readonly CloudCircuitBreaker _circuit;

    public GeminiChatProvider(ServerSettings settings, IHttpClientFactory httpFactory, ILogger<GeminiChatProvider> log)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _log = log;
        _circuit = new CloudCircuitBreaker("gemini", log);
    }

    public ModelSource Source => ModelSource.Gemini;

    public bool IsReady(CatalogEntry entry) => _settings.IsGeminiConfigured;

    public string? UnavailableReason(CatalogEntry entry) =>
        IsReady(entry) ? null : "Gemini API key is not configured. Open Server Settings → Cloud keys (global admin only).";

    public Task LoadAsync(CatalogEntry entry, string? localFilePath, CancellationToken ct)
    {
        if (!_settings.IsGeminiConfigured)
            throw new InvalidOperationException("Gemini API key is not configured.");
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
        var key = _settings.GetGeminiApiKey()
            ?? throw new InvalidOperationException("Gemini API key is not configured.");

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
            throw new InvalidOperationException($"Gemini returned {(int)resp.StatusCode}: {Truncate(err, 400)}");
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
                _log.LogDebug(ex, "Gemini: ignoring malformed SSE payload ({Snippet})", Truncate(payload, 120));
                continue;
            }

            if (!string.IsNullOrEmpty(token)) yield return token;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
