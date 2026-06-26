using Microsoft.Extensions.Logging;
using MyLocalAssistant.Server.Tools;

namespace MyLocalAssistant.Server.Skills;

/// <summary>
/// Executes a named skill: validates required tools are available, builds the
/// SkillContext, calls ISkill.ExecuteAsync, and returns the result.
/// </summary>
public sealed class SkillExecutor
{
    private readonly SkillRegistry _registry;
    private readonly ToolRegistry _tools;
    private readonly ILogger<SkillExecutor> _logger;

    public SkillExecutor(SkillRegistry registry, ToolRegistry tools, ILogger<SkillExecutor> logger)
    {
        _registry = registry;
        _tools    = tools;
        _logger   = logger;
    }

    public async Task<SkillResult> RunAsync(
        string skillId,
        SkillContext context,
        CancellationToken ct = default)
    {
        var skill = _registry.Find(skillId);
        if (skill is null)
            return SkillResult.Error($"Unknown skill '{skillId}'.");

        // Validate required tools
        var missing = skill.RequiredToolIds
            .Where(id => !_tools.TryGet(id, out _))
            .ToList();

        if (missing.Count > 0)
            return SkillResult.Error(
                $"Skill '{skillId}' requires missing tools: {string.Join(", ", missing)}.");

        // Build resolved tool map
        var resolvedTools = skill.RequiredToolIds
            .ToDictionary(id => id, id => { _tools.TryGet(id, out var t); return t!; });

        var enrichedContext = context with { Tools = resolvedTools };

        try
        {
            _logger.LogInformation("Executing skill {Id} for user {User}", skillId, context.Username);
            var result = await skill.ExecuteAsync(enrichedContext, ct);
            return result ?? SkillResult.Ok($"Skill '{skillId}' completed.");
        }
        catch (OperationCanceledException)
        {
            return SkillResult.Error("Skill execution was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Skill {Id} failed.", skillId);
            return SkillResult.Error($"Skill '{skillId}' failed: {ex.Message}");
        }
    }
}
