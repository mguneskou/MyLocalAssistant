using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyLocalAssistant.Server.Skills;

/// <summary>Outcome of a skill execution.</summary>
public sealed record SkillResult(
    bool IsError,
    string Summary,
    string? StructuredJson = null)
{
    private static readonly JsonSerializerOptions s_opts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SkillResult Ok(string summary, object? details = null) =>
        new(false, summary, Serialize("success", summary, details));

    public static SkillResult Error(string message) =>
        new(true, message, Serialize("error", message, null));

    private static string Serialize(string status, string summary, object? details) =>
        JsonSerializer.Serialize(
            details is null ? (object)new { status, summary } : new { status, summary, details },
            s_opts);
}
