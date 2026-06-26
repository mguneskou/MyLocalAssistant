using Microsoft.Extensions.Logging;
using MyLocalAssistant.Server.Tools;

namespace MyLocalAssistant.Server.Skills;

/// <summary>
/// Holds all registered skills.  Skills are registered as ISkill singletons in DI;
/// the registry aggregates them and provides lookup by id or category.
/// </summary>
public sealed class SkillRegistry
{
    private readonly IReadOnlyDictionary<string, ISkill> _byId;
    private readonly ILogger<SkillRegistry> _logger;

    public SkillRegistry(IEnumerable<ISkill> skills, ILogger<SkillRegistry> logger)
    {
        _logger = logger;
        var dict = new Dictionary<string, ISkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            if (!dict.TryAdd(skill.Id, skill))
                _logger.LogWarning("Duplicate skill id '{Id}' — second registration ignored.", skill.Id);
            else
                _logger.LogInformation("Skill registered: {Id} ({Name})", skill.Id, skill.Name);
        }
        _byId = dict;
    }

    public ISkill? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyCollection<ISkill> All() => (IReadOnlyCollection<ISkill>)_byId.Values;

    public IEnumerable<ISkill> ByCategory(string category) =>
        _byId.Values.Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<SkillManifest> Manifests() => _byId.Values.Select(s => s.Manifest);
}
