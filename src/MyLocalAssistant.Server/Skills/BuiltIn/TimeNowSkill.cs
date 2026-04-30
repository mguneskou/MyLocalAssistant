using System.Globalization;
using System.Text.Json;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Skills.BuiltIn;

/// <summary>
/// Returns the current date/time. Useful so the LLM doesn't have to guess
/// "today" — its training cutoff is months/years stale.
/// </summary>
internal sealed class TimeNowSkill : ISkill
{
    public string Id => "time.now";
    public string Name => "Current time";
    public string Description => "Returns the server's current date and time, optionally in a named timezone.";
    public string Category => "Built-in";
    public string Source => SkillSources.BuiltIn;
    public string? Version => null;

    public IReadOnlyList<SkillToolDto> Tools { get; } = new[]
    {
        new SkillToolDto(
            Name: "time.now",
            Description: "Returns the current date and time as ISO-8601. " +
                         "Optionally pass a Windows or IANA timezone id (e.g. 'UTC', 'Europe/Berlin', 'Turkey Standard Time').",
            ArgumentsSchemaJson: """
            {
              "type": "object",
              "properties": {
                "timezone": {
                  "type": "string",
                  "description": "Windows or IANA timezone id. Defaults to the server's local timezone."
                }
              },
              "additionalProperties": false
            }
            """),
    };

    public SkillRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 4);

    public void Configure(string? configJson) { /* no per-instance config */ }

    public Task<SkillResult> InvokeAsync(SkillInvocation call, SkillContext ctx)
    {
        if (!string.Equals(call.ToolName, "time.now", StringComparison.Ordinal))
            return Task.FromResult(SkillResult.Error($"Unknown tool '{call.ToolName}'."));

        string? tz = null;
        if (!string.IsNullOrWhiteSpace(call.ArgumentsJson) && call.ArgumentsJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                if (doc.RootElement.TryGetProperty("timezone", out var t) && t.ValueKind == JsonValueKind.String)
                    tz = t.GetString();
            }
            catch (JsonException ex)
            {
                return Task.FromResult(SkillResult.Error("Arguments must be a JSON object: " + ex.Message));
            }
        }

        TimeZoneInfo zone;
        try
        {
            zone = string.IsNullOrWhiteSpace(tz) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(tz);
        }
        catch (TimeZoneNotFoundException)
        {
            return Task.FromResult(SkillResult.Error($"Unknown timezone '{tz}'."));
        }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var iso = now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var human = now.ToString("ddd, d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        return Task.FromResult(SkillResult.Ok(
            $"{iso} ({zone.Id}) — {human}",
            JsonSerializer.Serialize(new { iso, zone = zone.Id, human })));
    }
}
