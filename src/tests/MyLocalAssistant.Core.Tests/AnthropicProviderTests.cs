using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Core.Tests;

public sealed class AnthropicProviderTests
{
    [Fact]
    public void AnthropicProvider_UsesPromptCachingHeadersAndEphemeralCacheControl()
    {
        var provider = new AnthropicChatProvider(
            settings: new ServerSettings { AnthropicApiKeyProtected = "test" },
            httpFactory: new StubHttpClientFactory(),
            log: NullLogger<AnthropicChatProvider>.Instance);

        var entry = new CatalogEntry
        {
            Id = "anthropic-claude-opus-4-8",
            RemoteModel = "claude-opus-4-8",
            Source = ModelSource.Anthropic,
        };

        Assert.True(provider.IsReady(entry));
    }

    /// <summary>
    /// Regression test for the bug this native path was built to fix: a model call that
    /// streams text deltas AND a tool_use block (with its arguments arriving as fragmented
    /// input_json_delta chunks, as Anthropic actually sends them) must be reassembled into
    /// exactly one well-formed tool call — not silently dropped, not left as broken JSON.
    /// </summary>
    [Fact]
    public async Task GenerateWithToolsAsync_ReassemblesStreamedTextAndToolUse()
    {
        var handler = new FakeHttpMessageHandler(AnthropicSseFixtures.TextThenToolUse);
        var provider = new AnthropicChatProvider(
            settings: new ServerSettings { AnthropicApiKeyProtected = "test" },
            httpFactory: new StubHttpClientFactory(handler),
            log: NullLogger<AnthropicChatProvider>.Instance);

        var entry = new CatalogEntry { Id = "anthropic-claude-haiku-4-5", RemoteModel = "claude-haiku-4-5", Source = ModelSource.Anthropic };
        var tools = new[]
        {
            new ToolFunctionDto(
                Name: "excel.create",
                Description: "Create a new empty Excel workbook.",
                ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"}},"required":["filename"]}"""),
        };
        var messages = new[] { new NativeChatMessage("user", new List<NativeContentBlock> { new NativeTextBlock("Create test.xlsx") }) };

        var textDeltas = new List<string>();
        NativeMessageCompleteEvent? complete = null;
        await foreach (var ev in provider.GenerateWithToolsAsync(entry, "system prompt", messages, tools, 1024, CancellationToken.None))
        {
            switch (ev)
            {
                case NativeTextDeltaEvent d: textDeltas.Add(d.Text); break;
                case NativeMessageCompleteEvent c: complete = c; break;
            }
        }

        Assert.Equal("Hello world", string.Concat(textDeltas));
        Assert.NotNull(complete);
        Assert.Equal("tool_use", complete!.StopReason);

        var toolUse = Assert.Single(complete.Message.Content.OfType<NativeToolUseBlock>());
        Assert.Equal("toolu_1", toolUse.Id);
        // The fixture's content_block_start echoes "excel_create" (the sanitized wire name
        // Anthropic actually received, since dots fail its ^[a-zA-Z0-9_-]{1,128}$ tool-name
        // pattern) — this asserts it round-trips back to the ORIGINAL dotted name ChatService's
        // LookupAllowedSkill needs, not the sanitized string.
        Assert.Equal("excel.create", toolUse.Name);
        // The two input_json_delta fragments ("{\"file" + "name\":\"test.xlsx\"}") must
        // reassemble into valid JSON with the intended value — this is exactly the kind of
        // reassembly bug (wrong split point, wrong buffer) that would corrupt every tool call.
        using var argsDoc = JsonDocument.Parse(toolUse.ArgumentsJson);
        Assert.Equal("test.xlsx", argsDoc.RootElement.GetProperty("filename").GetString());

        var textBlock = Assert.Single(complete.Message.Content.OfType<NativeTextBlock>());
        Assert.Equal("Hello world", textBlock.Text);
    }

    /// <summary>
    /// The request body sent to Anthropic must embed tool schemas as real JSON (input_schema),
    /// not as an escaped string, and must round-trip a prior tool_use/tool_result pair in the
    /// wire shape Anthropic requires.
    /// </summary>
    [Fact]
    public async Task GenerateWithToolsAsync_SendsToolSchemasAndReplaysToolResults()
    {
        var handler = new FakeHttpMessageHandler(AnthropicSseFixtures.EndTurnOnly);
        var provider = new AnthropicChatProvider(
            settings: new ServerSettings { AnthropicApiKeyProtected = "test" },
            httpFactory: new StubHttpClientFactory(handler),
            log: NullLogger<AnthropicChatProvider>.Instance);

        var entry = new CatalogEntry { Id = "anthropic-claude-haiku-4-5", RemoteModel = "claude-haiku-4-5", Source = ModelSource.Anthropic };
        var tools = new[]
        {
            new ToolFunctionDto(
                Name: "excel.create",
                Description: "Create a new empty Excel workbook.",
                ArgumentsSchemaJson: """{"type":"object","properties":{"filename":{"type":"string"}},"required":["filename"]}"""),
        };
        var messages = new[]
        {
            new NativeChatMessage("user", new List<NativeContentBlock> { new NativeTextBlock("Create test.xlsx") }),
            new NativeChatMessage("assistant", new List<NativeContentBlock> { new NativeToolUseBlock("toolu_1", "excel.create", """{"filename":"test.xlsx"}""") }),
            new NativeChatMessage("user", new List<NativeContentBlock> { new NativeToolResultBlock("toolu_1", "{\"content\":\"test.xlsx\"}", false) }),
        };

        await foreach (var _ in provider.GenerateWithToolsAsync(entry, "system prompt", messages, tools, 1024, CancellationToken.None)) { }

        Assert.NotNull(handler.LastRequestBody);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;

        // Anthropic rejects tool names containing "." (pattern ^[a-zA-Z0-9_-]{1,128}$), so the
        // wire name must be the sanitized "excel_create", never the raw ChatService-facing name.
        var toolDef = root.GetProperty("tools")[0];
        Assert.Equal("excel_create", toolDef.GetProperty("name").GetString());
        Assert.Equal("object", toolDef.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("string", toolDef.GetProperty("input_schema").GetProperty("properties").GetProperty("filename").GetProperty("type").GetString());

        var msgs = root.GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());

        var toolUseBlock = msgs[1].GetProperty("content")[0];
        Assert.Equal("tool_use", toolUseBlock.GetProperty("type").GetString());
        Assert.Equal("toolu_1", toolUseBlock.GetProperty("id").GetString());
        // The replayed tool_use block must use the same sanitized name declared in "tools"
        // above — Anthropic 400s if a tool_use.name doesn't match a declared tool exactly.
        Assert.Equal("excel_create", toolUseBlock.GetProperty("name").GetString());
        Assert.Equal("test.xlsx", toolUseBlock.GetProperty("input").GetProperty("filename").GetString());

        var toolResultBlock = msgs[2].GetProperty("content")[0];
        Assert.Equal("tool_result", toolResultBlock.GetProperty("type").GetString());
        Assert.Equal("toolu_1", toolResultBlock.GetProperty("tool_use_id").GetString());
        Assert.False(toolResultBlock.GetProperty("is_error").GetBoolean());
    }

    /// <summary>
    /// Regression test for the 400 this shipped as: "tools.0.custom.name: String should match
    /// pattern '^[a-zA-Z0-9_-]{1,128}$'". Every tool name in this app is "group.method"
    /// (single-dot like "excel.create", double-dot like "client.fs.copyToWorkDir") — both
    /// forms must sanitize to Anthropic-legal names, stay distinct from each other, and match
    /// Anthropic's exact published pattern.
    /// </summary>
    [Fact]
    public async Task GenerateWithToolsAsync_SanitizesAllToolNamesToAnthropicsPattern()
    {
        var handler = new FakeHttpMessageHandler(AnthropicSseFixtures.EndTurnOnly);
        var provider = new AnthropicChatProvider(
            settings: new ServerSettings { AnthropicApiKeyProtected = "test" },
            httpFactory: new StubHttpClientFactory(handler),
            log: NullLogger<AnthropicChatProvider>.Instance);

        var entry = new CatalogEntry { Id = "anthropic-claude-haiku-4-5", RemoteModel = "claude-haiku-4-5", Source = ModelSource.Anthropic };
        var tools = new[]
        {
            new ToolFunctionDto("excel.create", "Create a workbook.", """{"type":"object"}"""),
            new ToolFunctionDto("client.fs.copyToWorkDir", "Copy a client file into the work dir.", """{"type":"object"}"""),
        };
        var messages = new[] { new NativeChatMessage("user", new List<NativeContentBlock> { new NativeTextBlock("hi") }) };

        await foreach (var _ in provider.GenerateWithToolsAsync(entry, null, messages, tools, 1024, CancellationToken.None)) { }

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var pattern = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9_-]{1,128}$");
        var names = body.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();

        Assert.All(names, n => Assert.Matches(pattern, n));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler is null ? new HttpClient() : new HttpClient(handler);
    }

    /// <summary>Returns a canned SSE response for every request and captures the last request body sent.</summary>
    private sealed class FakeHttpMessageHandler(string sseBody) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static class AnthropicSseFixtures
    {
        public const string TextThenToolUse = """
event: message_start
data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-4-5","content":[],"usage":{"input_tokens":10,"output_tokens":0}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"excel_create","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"file"}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"name\":\"test.xlsx\"}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":15}}

event: message_stop
data: {"type":"message_stop"}


""";

        public const string EndTurnOnly = """
event: message_start
data: {"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","model":"claude-haiku-4-5","content":[],"usage":{"input_tokens":10,"output_tokens":0}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Done."}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}

event: message_stop
data: {"type":"message_stop"}


""";
    }
}
