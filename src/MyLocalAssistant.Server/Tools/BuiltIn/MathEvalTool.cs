using System.Text.Json;
using MyLocalAssistant.Shared.Contracts;
using NCalc;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Pure mathematical expression evaluator. Powered by NCalc — supports +, -, *, /, %,
/// parentheses, comparisons, common functions (Sin, Cos, Sqrt, Log, Round, Min, Max,
/// Abs, Pow, ...). No I/O, no side effects, no model calls. Safe to expose to any agent.
/// </summary>
internal sealed class MathEvalTool : ITool
{
    public string Id => "math.eval";
    public string Name => "Math evaluator";
    public string Description => "Evaluates a mathematical expression and returns the numeric result.";
    public string Category => "Built-in";
    public string Source => ToolSources.BuiltIn;
    public string? Version => null;
    public string? Publisher => "MyLocalAssistant";
    public string? KeyId => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "math.eval",
            Description: "Evaluate a math expression. Supports +, -, *, /, %, parentheses, " +
                         "comparisons, and functions like Sin, Cos, Sqrt, Log, Round, Pow, Abs, Min, Max.",
            ArgumentsSchemaJson: """
            {
              "type": "object",
              "required": ["expression"],
              "properties": {
                "expression": {
                  "type": "string",
                  "description": "The expression to evaluate, e.g. \"Sqrt(2) * 10 + Round(3.7)\"."
                }
              },
              "additionalProperties": false
            }
            """),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 4);

    public void Configure(string? configJson) { /* no per-instance config */ }

    public Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        if (!string.Equals(call.ToolName, "math.eval", StringComparison.Ordinal))
            return Task.FromResult(ToolResult.Error($"Unknown tool '{call.ToolName}'."));

        string expression;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            if (!doc.RootElement.TryGetProperty("expression", out var ex) || ex.ValueKind != JsonValueKind.String)
                return Task.FromResult(ToolResult.Error("Missing required string argument 'expression'."));
            expression = ex.GetString()!;
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ToolResult.Error("Arguments must be a JSON object: " + ex.Message));
        }

        if (string.IsNullOrWhiteSpace(expression))
            return Task.FromResult(ToolResult.Error("Expression must not be empty."));
        if (expression.Length > 1024)
            return Task.FromResult(ToolResult.Error("Expression is too long (limit 1024 chars)."));

        try
        {
            // NoCache: prevents storing user-supplied expressions in a process-wide cache.
            // IgnoreCase: convenience for the LLM (sin vs Sin).
            var expr = new Expression(expression, ExpressionOptions.NoCache | ExpressionOptions.IgnoreCaseAtBuiltInFunctions);
            var value = expr.Evaluate();
            return Task.FromResult(ToolResult.Ok(
                value?.ToString() ?? "null",
                JsonSerializer.Serialize(new { expression, value })));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or DivideByZeroException
                                     || ex.GetType().Namespace?.StartsWith("NCalc", StringComparison.Ordinal) == true)
        {
            return Task.FromResult(ToolResult.Error("Could not evaluate expression: " + ex.Message));
        }
    }
}
