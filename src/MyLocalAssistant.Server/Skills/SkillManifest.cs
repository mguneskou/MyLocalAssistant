namespace MyLocalAssistant.Server.Skills;

/// <summary>Declarative manifest for a skill — surfaced in the admin UI and REST API.</summary>
public sealed record SkillManifest(
    string Id,
    string Name,
    string Description,
    string Category,
    string Version,
    string Publisher,
    IReadOnlyList<SkillParameterDef> Inputs,
    IReadOnlyList<SkillParameterDef> Outputs,
    IReadOnlyList<string> RequiredToolIds,
    bool IsBuiltIn = true);

/// <summary>One parameter in a skill's input or output schema.</summary>
public sealed record SkillParameterDef(
    string Name,
    string Type,
    string Description,
    bool Required = false);
