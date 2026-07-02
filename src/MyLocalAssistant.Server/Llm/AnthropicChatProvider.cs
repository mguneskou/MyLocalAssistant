using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// <see cref="IChatProvider"/> implementation for the Anthropic Messages API. Also implements
/// <see cref="INativeToolChatProvider"/>: ChatService uses the native path (see
/// <see cref="ToolCallProtocols.Native"/>) rather than the text-tag <see cref="GenerateAsync"/>
/// path whenever tools are involved, since Claude reliably obeys Anthropic's own
/// <c>tools</c>/<c>tool_use</c> API but is not guaranteed to imitate the app's custom
/// <c>&lt;tool_call&gt;</c> text grammar. <see cref="GenerateAsync"/> is kept for plain
/// non-tool text completions (e.g. <c>MemorySummarizationService</c>).
/// Anthropic uses a different SSE event grammar than OpenAI:
/// <c>message_start</c>, <c>content_block_start/delta/stop</c>, <c>message_delta</c>,
/// <c>message_stop</c>; we only forward <c>content_block_delta</c> text deltas.
/// </summary>
public sealed class AnthropicChatProvider : IChatProvider, INativeToolChatProvider
{
    private const string BaseUrl = "https://api.anthropic.com/v1";
    private const string ApiVersion = "2023-06-01";
    private const string PromptCachingBeta = "prompt-caching-2024-07-31";

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
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = prompt,
                            cache_control = new { type = "ephemeral" },
                        },
                    },
                },
            },
            stop_sequences = stopArr,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/messages")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", key);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        req.Headers.TryAddWithoutValidation("anthropic-beta", PromptCachingBeta);
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

    public async IAsyncEnumerable<NativeChatEvent> GenerateWithToolsAsync(
        CatalogEntry entry,
        string? systemPrompt,
        IReadOnlyList<NativeChatMessage> messages,
        IReadOnlyList<ToolFunctionDto> tools,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in _circuit.ExecuteAsync(
            () => GenerateWithToolsCoreAsync(entry, systemPrompt, messages, tools, maxTokens, ct), ct).ConfigureAwait(false))
            yield return ev;
    }

    private async IAsyncEnumerable<NativeChatEvent> GenerateWithToolsCoreAsync(
        CatalogEntry entry,
        string? systemPrompt,
        IReadOnlyList<NativeChatMessage> messages,
        IReadOnlyList<ToolFunctionDto> tools,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = _settings.GetAnthropicApiKey()
            ?? throw new InvalidOperationException("Anthropic API key is not configured.");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var modelName = string.IsNullOrWhiteSpace(entry.RemoteModel) ? entry.Id : entry.RemoteModel;

        var root = new JsonObject
        {
            ["model"] = modelName,
            ["stream"] = true,
            ["max_tokens"] = maxTokens,
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            root["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = systemPrompt,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            });
        }

        // Anthropic's tool name pattern is ^[a-zA-Z0-9_-]{1,128}$ — every tool in this app is
        // named "group.method" (e.g. "excel.create", "client.fs.copyToWorkDir"), so the raw
        // name is always rejected with a 400. Sanitize for the wire and keep a reverse map so
        // tool_use blocks read back from the stream (and any of this turn's tool_use blocks
        // replayed into a later request) round-trip to the original name ChatService expects.
        var (toWire, toOriginal) = BuildToolNameMaps(tools);

        root["messages"] = BuildMessagesArray(messages, toWire);

        if (tools.Count > 0)
        {
            var toolsArray = new JsonArray();
            foreach (var t in tools)
            {
                JsonNode? schemaNode;
                try
                {
                    schemaNode = JsonNode.Parse(t.ArgumentsSchemaJson);
                }
                catch (JsonException)
                {
                    schemaNode = new JsonObject { ["type"] = "object" };
                }
                toolsArray.Add(new JsonObject
                {
                    ["name"] = toWire[t.Name],
                    ["description"] = t.Description,
                    ["input_schema"] = schemaNode,
                });
            }
            root["tools"] = toolsArray;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/messages")
        {
            Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", key);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        req.Headers.TryAddWithoutValidation("anthropic-beta", PromptCachingBeta);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Anthropic returned {(int)resp.StatusCode}: {Truncate(err, 400)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Content blocks (text / tool_use) are streamed by index; input_json_delta fragments
        // must be concatenated verbatim (they are NOT independently valid JSON) and parsed
        // only once the block closes.
        var blocks = new SortedDictionary<int, NativeBlockBuilder>();
        var stopReason = "end_turn";
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

            var payload = line.Substring(5).Trim();
            if (payload.Length == 0) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                _log.LogDebug(ex, "Anthropic: ignoring malformed SSE payload ({Snippet})", Truncate(payload, 120));
                continue;
            }

            using (doc)
            {
                var evRoot = doc.RootElement;
                switch (currentEvent)
                {
                    case "content_block_start":
                    {
                        var index = evRoot.GetProperty("index").GetInt32();
                        var cb = evRoot.GetProperty("content_block");
                        var type = cb.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                        blocks[index] = type == "tool_use"
                            ? new NativeBlockBuilder
                            {
                                IsToolUse = true,
                                Id = cb.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                                // Anthropic echoes back the sanitized wire name we sent it —
                                // map it back to the original dotted tool name.
                                Name = cb.TryGetProperty("name", out var nameEl)
                                    ? MapToOriginal(nameEl.GetString() ?? "", toOriginal)
                                    : "",
                            }
                            : new NativeBlockBuilder();
                        break;
                    }
                    case "content_block_delta":
                    {
                        var index = evRoot.GetProperty("index").GetInt32();
                        var delta = evRoot.GetProperty("delta");
                        var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                        if (deltaType == "text_delta" && delta.TryGetProperty("text", out var textEl))
                        {
                            var t = textEl.GetString() ?? "";
                            if (blocks.TryGetValue(index, out var b)) b.Text.Append(t);
                            if (t.Length > 0) yield return new NativeTextDeltaEvent(t);
                        }
                        else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var pjEl))
                        {
                            if (blocks.TryGetValue(index, out var b)) b.Json.Append(pjEl.GetString() ?? "");
                        }
                        break;
                    }
                    case "message_delta":
                    {
                        if (evRoot.TryGetProperty("delta", out var d)
                            && d.TryGetProperty("stop_reason", out var sr)
                            && sr.ValueKind == JsonValueKind.String)
                        {
                            stopReason = sr.GetString() ?? stopReason;
                        }
                        break;
                    }
                    case "message_stop":
                    {
                        var content = new List<NativeContentBlock>();
                        foreach (var (_, b) in blocks)
                        {
                            if (b.IsToolUse)
                                content.Add(new NativeToolUseBlock(b.Id, b.Name, b.Json.Length > 0 ? b.Json.ToString() : "{}"));
                            else if (b.Text.Length > 0)
                                content.Add(new NativeTextBlock(b.Text.ToString()));
                        }
                        yield return new NativeMessageCompleteEvent(new NativeChatMessage("assistant", content), stopReason);
                        yield break;
                    }
                }
            }
        }
    }

    /// <summary>Converts ChatService's provider-agnostic message list into Anthropic wire format.</summary>
    private static JsonArray BuildMessagesArray(IReadOnlyList<NativeChatMessage> messages, IReadOnlyDictionary<string, string> toWire)
    {
        var messagesArray = new JsonArray();
        foreach (var msg in messages)
        {
            var contentArray = new JsonArray();
            foreach (var block in msg.Content)
            {
                switch (block)
                {
                    case NativeTextBlock text when text.Text.Length > 0:
                        contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                        break;
                    case NativeToolUseBlock toolUse:
                    {
                        JsonNode? inputNode;
                        try
                        {
                            inputNode = JsonNode.Parse(string.IsNullOrWhiteSpace(toolUse.ArgumentsJson) ? "{}" : toolUse.ArgumentsJson);
                        }
                        catch (JsonException)
                        {
                            inputNode = new JsonObject();
                        }
                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = toolUse.Id,
                            // Must match the sanitized name declared in this same request's
                            // "tools" array, not the original dotted ChatService-facing name.
                            ["name"] = toWire.TryGetValue(toolUse.Name, out var wireName) ? wireName : SanitizeToolName(toolUse.Name),
                            ["input"] = inputNode,
                        });
                        break;
                    }
                    case NativeToolResultBlock toolResult:
                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = toolResult.ToolUseId,
                            ["content"] = toolResult.Content,
                            ["is_error"] = toolResult.IsError,
                        });
                        break;
                }
            }
            // Anthropic rejects messages with an empty content array — skip any that reduce to nothing.
            if (contentArray.Count > 0)
                messagesArray.Add(new JsonObject { ["role"] = msg.Role, ["content"] = contentArray });
        }
        return messagesArray;
    }

    /// <summary>
    /// Builds a deterministic, collision-safe mapping between ChatService's original tool names
    /// (dotted, e.g. "excel.create") and Anthropic-legal wire names (<c>^[a-zA-Z0-9_-]{1,128}$</c>).
    /// Recomputed fresh from the same <paramref name="tools"/> list on every call in a chat turn's
    /// tool loop — since the input list and order never change mid-turn, this reproduces the exact
    /// same mapping every time without needing to persist any state between calls.
    /// </summary>
    private static (Dictionary<string, string> ToWire, Dictionary<string, string> ToOriginal) BuildToolNameMaps(
        IReadOnlyList<ToolFunctionDto> tools)
    {
        var toWire = new Dictionary<string, string>(StringComparer.Ordinal);
        var toOriginal = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tools)
        {
            var baseName = SanitizeToolName(t.Name);
            var candidate = baseName;
            for (var suffix = 1; toOriginal.TryGetValue(candidate, out var existing) && existing != t.Name; suffix++)
            {
                candidate = $"{baseName}_{suffix}";
                if (candidate.Length > 128) candidate = candidate[..128];
            }
            toWire[t.Name] = candidate;
            toOriginal[candidate] = t.Name;
        }
        return (toWire, toOriginal);
    }

    private static string MapToOriginal(string wireName, IReadOnlyDictionary<string, string> toOriginal) =>
        toOriginal.TryGetValue(wireName, out var original) ? original : wireName;

    /// <summary>Replaces every character outside <c>[a-zA-Z0-9_-]</c> with <c>_</c> and caps length at 128.</summary>
    private static string SanitizeToolName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '_' or '-' ? c : '_');
        if (sb.Length == 0) sb.Append('_');
        return sb.Length > 128 ? sb.ToString(0, 128) : sb.ToString();
    }

    /// <summary>Accumulator for one streamed content block until its <c>content_block_stop</c>.</summary>
    private sealed class NativeBlockBuilder
    {
        public bool IsToolUse;
        public string Id = "";
        public string Name = "";
        public StringBuilder Text { get; } = new();
        public StringBuilder Json { get; } = new();
    }
}
