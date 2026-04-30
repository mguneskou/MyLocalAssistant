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
/// <see cref="IChatProvider"/> implementation that streams from the OpenAI
/// chat-completions API. Honors the optional <see cref="ServerSettings.OpenAiBaseUrl"/>
/// override so the same code path serves OpenAI proper, Azure OpenAI behind a gateway,
/// or any OpenAI-compatible local server (vLLM, Ollama, LM Studio, Together, Groq).
/// </summary>
public sealed class OpenAiChatProvider : IChatProvider
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";

    private readonly ServerSettings _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OpenAiChatProvider> _log;

    public OpenAiChatProvider(ServerSettings settings, IHttpClientFactory httpFactory, ILogger<OpenAiChatProvider> log)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _log = log;
    }

    public ModelSource Source => ModelSource.OpenAi;

    public bool IsReady(CatalogEntry entry) => _settings.IsOpenAiConfigured;

    public string? UnavailableReason(CatalogEntry entry) =>
        IsReady(entry) ? null : "OpenAI API key is not configured. Open Server Settings → Cloud keys (global admin only).";

    public Task LoadAsync(CatalogEntry entry, string? localFilePath, CancellationToken ct)
    {
        if (!_settings.IsOpenAiConfigured)
            throw new InvalidOperationException("OpenAI API key is not configured.");
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
        var key = _settings.GetOpenAiApiKey()
            ?? throw new InvalidOperationException("OpenAI API key is not configured.");
        var baseUrl = string.IsNullOrWhiteSpace(_settings.OpenAiBaseUrl)
            ? DefaultBaseUrl
            : _settings.OpenAiBaseUrl!.TrimEnd('/');

        var http = _httpFactory.CreateClient();
        // Long timeout so streaming generations aren't cut short by the default 100s.
        http.Timeout = TimeSpan.FromMinutes(10);

        var modelName = string.IsNullOrWhiteSpace(entry.RemoteModel) ? entry.Id : entry.RemoteModel;
        var stopArr = stops is { Count: > 0 } ? stops.Take(4).ToArray() : null;
        var body = new
        {
            model = modelName,
            stream = true,
            max_tokens = maxTokens,
            // Single user message carrying the fully-rendered prompt that ChatService built.
            // ChatService's tool-call grammar (<tool_call>…</tool_call>) is preserved verbatim.
            messages = new object[]
            {
                new { role = "user", content = prompt },
            },
            stop = stopArr,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"OpenAI returned {(int)resp.StatusCode}: {Truncate(err, 400)}");
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
                _log.LogDebug(ex, "OpenAI: ignoring malformed SSE payload ({Snippet})", Truncate(payload, 120));
                continue;
            }

            if (!string.IsNullOrEmpty(token)) yield return token;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
