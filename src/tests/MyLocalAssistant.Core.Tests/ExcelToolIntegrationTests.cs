using System.Text.Json;
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Server.Tools.BuiltIn;

namespace MyLocalAssistant.Core.Tests;

public sealed class ExcelToolIntegrationTests
{
    [Fact]
    public async Task RepairOpenXml_on_real_file_should_succeed_or_return_error()
    {
        var src = "D:\\MyLocalAssistant_working_space\\01.06.2026_BASEL_AYLIK.XLSX";
        if (!File.Exists(src))
            return; // skip when file isn't present in test environment

        var workDir = CreateTempDirectory();
        try
        {
            var dest = Path.Combine(workDir, Path.GetFileName(src));
            File.Copy(src, dest);

            var tool = new ExcelTool();
            var args = JsonSerializer.Serialize(new { filename = Path.GetFileName(src) });
            var ctx = MakeContext(workDir);

            var result = await tool.InvokeAsync(new ToolInvocation("excel.repair_openxml", args), ctx);

            // Write result to console for diagnostics when running tests interactively
            Console.WriteLine("IsError: " + result.IsError);
            Console.WriteLine(result.Content);

            // Do not fail the test — we want to observe the tool output
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static ToolContext MakeContext(string workDir) => new(
        UserId: Guid.NewGuid(),
        Username: "tester",
        IsAdmin: false,
        IsGlobalAdmin: false,
        AgentId: "agent-1",
        ConversationId: Guid.NewGuid(),
        WorkDirectory: workDir,
        CancellationToken: default);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mla-workdir-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
