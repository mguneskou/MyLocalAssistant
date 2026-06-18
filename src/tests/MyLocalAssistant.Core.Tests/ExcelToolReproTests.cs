using System.Text.Json;
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Server.Tools.BuiltIn;

namespace MyLocalAssistant.Core.Tests;

public sealed class ExcelToolReproTests
{
    [Fact]
    public async Task Reproduce_Varan_KeyNotFoundException_using_read_range_and_recalculate()
    {
        var src = "D:\\MyLocalAssistant_working_space\\01.06.2026_BASEL_AYLIK.XLSX";
        if (!File.Exists(src))
            return; // skip when not available

        var workDir = CreateTempDirectory();
        try
        {
            var dest = Path.Combine(workDir, Path.GetFileName(src));
            File.Copy(src, dest);

            var tool = new ExcelTool();
            var ctx = MakeContext(workDir);

            // 1) read_range (no range) - may trigger workbook load and KeyNotFoundException
            var argsRead = JsonSerializer.Serialize(new { filename = Path.GetFileName(src) });
            var readRes = await tool.InvokeAsync(new ToolInvocation("excel.read_range", argsRead), ctx);
            Console.WriteLine("read_range IsError: " + readRes.IsError);
            Console.WriteLine(readRes.Content);

            // 2) recalculate - may trigger formula evaluation
            var argsRec = JsonSerializer.Serialize(new { filename = Path.GetFileName(src) });
            var recRes = await tool.InvokeAsync(new ToolInvocation("excel.recalculate", argsRec), ctx);
            Console.WriteLine("recalculate IsError: " + recRes.IsError);
            Console.WriteLine(recRes.Content);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Reproduce_with_autofit_and_create_table()
    {
        var src = "D:\\MyLocalAssistant_working_space\\01.06.2026_BASEL_AYLIK.XLSX";
        if (!File.Exists(src)) return;
        var workDir = CreateTempDirectory();
        try
        {
            var dest = Path.Combine(workDir, Path.GetFileName(src));
            File.Copy(src, dest);
            var tool = new ExcelTool();
            var ctx = MakeContext(workDir);

            var argsAuto = JsonSerializer.Serialize(new { filename = Path.GetFileName(src), rows = false });
            var autoRes = await tool.InvokeAsync(new ToolInvocation("excel.auto_fit", argsAuto), ctx);
            Console.WriteLine("auto_fit IsError: " + autoRes.IsError);
            Console.WriteLine(autoRes.Content);

            var argsTable = JsonSerializer.Serialize(new { filename = Path.GetFileName(src), range = "A1:Z20" });
            var tblRes = await tool.InvokeAsync(new ToolInvocation("excel.create_table", argsTable), ctx);
            Console.WriteLine("create_table IsError: " + tblRes.IsError);
            Console.WriteLine(tblRes.Content);
        }
        finally { try { Directory.Delete(workDir, recursive: true); } catch { } }
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
        var path = Path.Combine(Path.GetTempPath(), "mla-workdir-repro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
