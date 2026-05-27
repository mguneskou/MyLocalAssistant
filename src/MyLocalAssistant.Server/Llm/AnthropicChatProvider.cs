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
/// <see cref="IChatProvider"/> implementation for the Anthropic Messages API.
/// Anthropic uses a different SSE event grammar than OpenAI:
/// <c>message_start</c>, <c>content_block_start/delta/stop</c>, <c>message_delta</c>,
/// <c>message_stop</c>; we only forward <c>content_block_delta</c> text deltas.
/// </summary>
public sealed class AnthropicChatProvider : IChatProvider
{
    private const string BaseUrl = "https://api.anthropic.com/v1";
    private const string ApiVersion = "2023-06-01";

    private readonly ServerSettings _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AnthropicChatProvider> _log;
    private readonly CloudCircuitBreaker _circuit;

    public AnthropicChatProvider(ServerSettings settings, IHttpClientFactory httpFactory, ILogger<AnthropicChatProvider> log)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _log = log;
        _circuit = new CloudCircuitBreaker("anthropic", log);
    }

    public ModelSource Source => ModelSource.Anthropic;

    public bool IsReady(CatalogEntry entry) => _settings.IsAnthropicConfigured;

    public string? UnavailableReason(CatalogEntry entry) =>
        IsReady(entry) ? null : "Anthropic API key is not configured. Open Server Settings → Cloud keys (global admin only).";

    public Task LoadAsync(CatalogEntry entry, string? localFilePath, CancellationToken ct)
    {
        if (!_settings.IsAnthropicConfigured)
            throw new InvalidOperationException("Anthropic API key is not configured.");
        return Task.CompletedTask;
    }

    public Task UnloadAsync() => Task.CompletedTask;

    public async IAsyncEnumerable<string> GenerateAsync(
        CatalogEntry entry,
        string prompt,
        int maxTokens,
        IReadOnlyList<string> stops,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var token in _circuit.ExecuteAsync(() => GenerateCoreAsync(entry, prompt, maxTokens, stops, ct), ct).ConfigureAwait(false))
            yield return token;
    }

    private async IAsyncEnumerable<string> GenerateCoreAsync(
        CatalogEntry entry,
        string prompt,
        int maxTokens,
        IReadOnlyList<string> stops,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = _settings.GetAnthropicApiKey()
            ?? throw new InvalidOperationException("Anthropic API key is not configured.");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var modelName = string.IsNullOrWhiteSpace(entry.RemoteModel) ? entry.Id : entry.RemoteModel;
        var stopArr = stops is { Count: > 0 } ? stops.Take(4).ToArray() : null;
        var body = new
        {
            model = modelName,
            stream = true,
            max_tokens = maxTokens,
            messages = new object[]
            {
                new { role = "user", content = prompt },
            },
            stop_sequences = stopArr,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/messages")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", key);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Anthropic returned {(int)resp.StatusCode}: {Truncate(err, 400)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Anthropic frames are: "event: <name>\n" then "data: <json>\n\n". The event name
        // we care about is "content_block_delta" with delta.type == "text_delta". Other
        // events are status/usage which we discard.
        string? currentEvent = null;
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) { currentEvent = null; continue; }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line.Substring(6).Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (currentEvent != "content_block_delta") continue;

            var payload = line.Substring(5).Trim();
            if (payload.Length == 0) continue;

            string? token = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("type", out var typ)
                    && typ.ValueKind == JsonValueKind.String
                    && typ.GetString() == "text_delta"
                    && delta.TryGetProperty("text", out var txt)
                    && txt.ValueKind == JsonValueKind.String)
                {
                    token = txt.GetString();
                }
            }
            catch (JsonException ex)
            {
                _log.LogDebug(ex, "Anthropic: ignoring malformed SSE payload ({Snippet})", Truncate(payload, 120));
                continue;
            }

            if (!string.IsNullOrEmpty(token)) yield return token;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
