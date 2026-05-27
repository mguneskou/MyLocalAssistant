using System.Reflection;
using MyLocalAssistant.Server.Llm;

namespace MyLocalAssistant.Core.Tests;

public class BuildCeoModeAComplianceSupplementIfNeededTests
{
    private const string CeoAgentId = "ceo-strategic-supervisor";
    private const string OtherAgentId = "general-assistant";

    private static readonly MethodInfo? s_method = typeof(ChatService).GetMethod(
        "BuildCeoModeAComplianceSupplementIfNeeded",
        BindingFlags.NonPublic | BindingFlags.Static);

    [Theory]
    [MemberData(nameof(MissingSchemaCases))]
    public void Missing_schema_cases_report_expected_lines(
        string assistantText,
        string? expectedSectionsLine,
        string? expectedFieldsLine)
    {
        var supplement = Invoke(CeoAgentId, assistantText);

        Assert.Contains("[Mode A schema compliance notice]", supplement, StringComparison.Ordinal);

        if (expectedSectionsLine is null)
            Assert.DoesNotContain("Missing sections:", supplement, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains(expectedSectionsLine, supplement, StringComparison.Ordinal);

        if (expectedFieldsLine is null)
            Assert.DoesNotContain("Missing required fields:", supplement, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains(expectedFieldsLine, supplement, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_ceo_agent_never_gets_supplement()
    {
        var supplement = Invoke(OtherAgentId, "Top risks");
        Assert.Equal(string.Empty, supplement);
    }

    [Fact]
    public void Complete_schema_is_idempotent_and_produces_no_supplement()
    {
        var complete = CompleteModeAText();

        var first = Invoke(CeoAgentId, complete);
        var second = Invoke(CeoAgentId, complete);

        Assert.Equal(string.Empty, first);
        Assert.Equal(string.Empty, second);
    }

    public static IEnumerable<object?[]> MissingSchemaCases()
    {
        yield return
        [
            string.Empty,
            "Missing sections: Executive recommendation, Top risks, Top opportunities, Evidence map, Evidence gaps and assumptions, Remediation actions.",
            "Missing required fields: Decision, Confidence.",
        ];

        yield return
        [
            "Executive recommendation\nTop risks\nTop opportunities\nEvidence map\nEvidence gaps and assumptions\nRemediation actions",
            null,
            "Missing required fields: Decision, Confidence.",
        ];

        yield return
        [
            "Executive recommendation\nTop risks\nEvidence map\nEvidence gaps and assumptions\nRemediation actions\nDecision: Approve\nConfidence: Medium",
            "Missing sections: Top opportunities.",
            null,
        ];

        yield return
        [
            "Executive recommendation\nTop risks\nTop opportunities\nEvidence map\nEvidence gaps and assumptions\nRemediation actions\nDecision: Conditional Approve",
            null,
            "Missing required fields: Confidence.",
        ];
    }

    private static string Invoke(string agentId, string assistantText)
    {
        Assert.NotNull(s_method);
        var result = s_method!.Invoke(null, new object[] { agentId, assistantText });
        return Assert.IsType<string>(result);
    }

    private static string CompleteModeAText() =>
        """
        Executive recommendation
        Decision: Approve
        Confidence: High
        Top risks
        Top opportunities
        Evidence map
        Evidence gaps and assumptions
        Remediation actions
        """;
}
