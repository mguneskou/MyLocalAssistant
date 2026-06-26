using MyLocalAssistant.Server.Skills;

namespace MyLocalAssistant.Server.Skills.Development;

/// <summary>
/// Hermes-style "write a Python script when needed" skill.
///
/// This skill injects a strong system prompt that instructs the LLM to autonomously
/// decide to write and run a Python script whenever:
///   - the user asks to read, parse, or transform a file
///   - the user asks for data processing, calculations, or analysis
///   - no other built-in tool can satisfy the request
///
/// The LLM writes the script, calls python.run, observes the output, and iterates
/// until the task is complete — exactly like Hermes or GitHub Copilot Workspace.
/// </summary>
public sealed class PythonScriptSkill : ISkill
{
    public string Id          => "python-script";
    public string Name        => "Python Script";
    public string Description =>
        "Automatically writes and executes Python scripts to handle tasks that built-in tools cannot — " +
        "file reading, data parsing, format conversion, calculations, and more.";
    public string Category    => "Development";

    public string SystemPrompt => """
        You are an intelligent assistant that can write and execute Python scripts on demand.

        When to write a Python script:
        - The user asks you to read, inspect, or extract information from a file (CSV, JSON, XML, Excel, text, log, etc.)
        - The user asks for data processing, transformation, calculation, or analysis
        - The user asks you to compare, merge, or summarise data from multiple sources
        - No existing built-in tool (Excel reader, PDF reader, SQL, etc.) can handle the request directly
        - The user explicitly asks you to write code or a script

        How to proceed:
        1. Think about what the script needs to do.
        2. Write a clean, complete Python script.
        3. Use `print()` for all output — this is the only way to return results.
        4. Use `WORK_DIR` (already set) as the base path for reading/writing files.
        5. Call the `python.run` tool with the script.
        6. Read the output carefully.
        7. If there is an error, fix the script and try again.
        8. When done, summarise the result clearly for the user.

        Rules:
        - Prefer stdlib over third-party packages; use `os`, `csv`, `json`, `pathlib`, `re`, `collections` freely.
        - If you need `pandas` or `openpyxl`, try importing them — they may be available.
        - Never write to paths outside WORK_DIR unless the user explicitly provides a target path.
        - Always show the final result to the user, not just "script ran successfully".
        - If the task is clearly impossible in Python (e.g. accessing the internet when blocked), tell the user why.
        """;

    public IReadOnlyList<string> RequiredToolIds => ["code.python"];

    public SkillManifest Manifest { get; } = new(
        Id:          "python-script",
        Name:        "Python Script",
        Description: "Writes and runs Python scripts on demand for file processing, data analysis, and tasks beyond built-in tools.",
        Category:    "Development",
        Version:     "1.0.0",
        Publisher:   "MyLocalAssistant",
        Inputs:
        [
            new("task", "string", "Description of what the script should do.", Required: true),
        ],
        Outputs:
        [
            new("result", "string", "Output produced by the script."),
        ],
        RequiredToolIds: ["code.python"]);

    public Task<SkillResult?> ExecuteAsync(SkillContext context, CancellationToken ct)
        => Task.FromResult<SkillResult?>(null); // fully LLM-driven via SystemPrompt + python.run tool
}
