using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Models;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Core.Tests;

/// <summary>
/// Guards the model-id → tool-calling-protocol mapping. A miss here silently disables
/// tool calling for the affected model (ResolveSkills returns nothing → toolMode=false →
/// the prompt never advertises tools), which is invisible until a user tries to use a tool.
/// The regression this pins: cloud ids (openai-*, anthropic-*) and several local families
/// (gemma*, llama32*, phi4*, ...) were absent from the built-in table and defaulted to None.
/// </summary>
public class ModelCapabilityRegistryTests
{
    private static string CatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "model-catalog.json");

    private static ModelCapabilityRegistry NewRegistry()
    {
        // Point ContentRootPath at an empty temp dir so no config/model-capabilities.json
        // override is picked up — we exercise the built-in defaults only.
        var env = new StubEnv { ContentRootPath = Path.GetTempPath() };
        return new ModelCapabilityRegistry(NullLogger<ModelCapabilityRegistry>.Instance, env);
    }

    /// <summary>
    /// Chat models we intentionally leave without tool support, resolving to None so the user
    /// gets a clean "model does not support tool calling" message instead of broken calls.
    /// Keep this list tiny and justified:
    ///  - TinyLlama 1.1B: too small to reliably emit a well-formed &lt;tool_call&gt; block.
    ///  - SmolLM2 1.7B: no robust native tool template (limited tool calling).
    ///  - OLMo-2 13B: no native tool template.
    /// </summary>
    private static readonly HashSet<string> IntentionallyNoTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "tinyllama-1.1b-q5km",
            "smollm2-1.7b-instruct-q4km",
            "olmo-2-1124-13b-instruct-q4km",
        };

    private static IReadOnlyList<CatalogEntry> ChatModels()
    {
        using var s = File.OpenRead(CatalogPath);
        return ModelCatalogService.LoadFromStream(s).Entries
            .Where(e => e.Tier != ModelTier.Embedding)
            .Where(e => !IntentionallyNoTools.Contains(e.Id))
            .ToArray();
    }

    [Fact]
    public void Every_shipped_chat_model_supports_a_tool_calling_protocol()
    {
        var reg = NewRegistry();

        var withoutTools = ChatModels()
            .Where(e => reg.Get(e.Id).Tools == ToolCallProtocols.None)
            .Select(e => e.Id)
            .ToArray();

        Assert.True(
            withoutTools.Length == 0,
            "These shipped chat models resolve to ToolCallProtocols.None, so tool calling is " +
            "silently disabled for them. Add matching patterns to ModelCapabilityRegistry: " +
            string.Join(", ", withoutTools));
    }

    [Theory]
    [InlineData("openai-gpt-4.1")]
    [InlineData("openai-gpt-4o-mini")]
    [InlineData("gemma3-12b-q4km")]
    [InlineData("llama32-3b-q4km")]
    [InlineData("phi4-mini-q4km")]
    [InlineData("qwen25-coder-7b-instruct-q4km")]
    [InlineData("gpt-oss-20b-q4km")]
    [InlineData("cerebras-glm-4.7")]
    public void Cloud_and_previously_missing_local_ids_resolve_to_tags(string modelId)
    {
        Assert.Equal(ToolCallProtocols.Tags, NewRegistry().Get(modelId).Tools);
    }

    /// <summary>
    /// Anthropic models resolve to Native, not Tags: Claude Haiku 4.5 was observed fabricating
    /// an entire fake &lt;function_calls&gt;/&lt;tool_result&gt; exchange and reporting success
    /// while the tags parser never recognized the tag and the real tool never ran. Native mode
    /// uses Claude's own tools/tool_use API (see INativeToolChatProvider) so there is no custom
    /// grammar for the model to drift away from.
    /// </summary>
    [Theory]
    [InlineData("anthropic-claude-sonnet-5")]
    [InlineData("anthropic-claude-fable-5")]
    [InlineData("anthropic-claude-opus-4-8")]
    [InlineData("anthropic-claude-haiku-4-5")]
    public void Anthropic_ids_resolve_to_native(string modelId)
    {
        Assert.Equal(ToolCallProtocols.Native, NewRegistry().Get(modelId).Tools);
    }

    [Fact]
    public void Unknown_model_id_defaults_to_none()
    {
        Assert.Equal(ToolCallProtocols.None, NewRegistry().Get("totally-unknown-model-xyz").Tools);
    }

    private sealed class StubEnv : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
