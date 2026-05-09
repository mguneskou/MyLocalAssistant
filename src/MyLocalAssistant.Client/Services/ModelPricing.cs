namespace MyLocalAssistant.Client.Services;

/// <summary>
/// Rough pricing table keyed on model-ID prefix (lower-case).
/// Prices are per 1 000 tokens in USD (input and output averaged).
/// Rates are approximate and based on publicly listed prices.
/// </summary>
public static class ModelPricing
{
    // (input $/1K tokens, output $/1K tokens)
    private static readonly (string Prefix, decimal Input, decimal Output)[] _table =
    [
        // OpenAI
        ("gpt-4o-mini",             0.00015m, 0.0006m),
        ("gpt-4o",                  0.005m,   0.015m),
        ("gpt-4-turbo",             0.01m,    0.03m),
        ("gpt-4",                   0.03m,    0.06m),
        ("gpt-3.5-turbo",           0.0005m,  0.0015m),
        ("o1-mini",                 0.003m,   0.012m),
        ("o1",                      0.015m,   0.06m),
        // Anthropic
        ("claude-3-5-sonnet",       0.003m,   0.015m),
        ("claude-3-5-haiku",        0.0008m,  0.004m),
        ("claude-3-opus",           0.015m,   0.075m),
        ("claude-3-haiku",          0.00025m, 0.00125m),
        ("claude-3-sonnet",         0.003m,   0.015m),
        // Groq (LPU pricing)
        ("llama-3.3-70b",           0.00059m, 0.00079m),
        ("llama-3.1-70b",           0.00059m, 0.00079m),
        ("llama-3.1-8b",            0.00005m, 0.00008m),
        ("mixtral-8x7b",            0.00027m, 0.00027m),
        ("gemma2-9b",               0.0002m,  0.0002m),
        // Gemini
        ("gemini-2.0-flash",        0.0001m,  0.0004m),
        ("gemini-1.5-flash",        0.000075m,0.0003m),
        ("gemini-1.5-pro",          0.00125m, 0.005m),
        // Mistral
        ("mistral-small",           0.0002m,  0.0006m),
        ("mistral-medium",          0.0027m,  0.0081m),
        ("mistral-large",           0.002m,   0.006m),
        ("codestral",               0.001m,   0.003m),
        // Cerebras
        ("llama3.1-70b",            0.0006m,  0.0006m),
        ("llama3.1-8b",             0.0001m,  0.0001m),
    ];

    /// <summary>
    /// Estimates cost in USD for the given model and approximate token counts.
    /// Returns null for local models (no model ID) or unknown models (cost = $0).
    /// </summary>
    public static decimal? Estimate(string? modelId, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var key = modelId.ToLowerInvariant();
        foreach (var (prefix, inputRate, outputRate) in _table)
        {
            if (key.Contains(prefix))
                return (inputTokens * inputRate + outputTokens * outputRate) / 1000m;
        }
        return null; // unknown model — don't guess
    }

    /// <summary>
    /// Formats an estimated cost as a short string for display (e.g. "~$0.0012").
    /// Returns empty string if <paramref name="cost"/> is null.
    /// </summary>
    public static string Format(decimal? cost) =>
        cost is null ? "" : $"~${cost.Value:F4}";
}
