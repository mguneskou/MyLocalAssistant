using System.Text.Json;
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Server.Tools.BuiltIn;

namespace MyLocalAssistant.Core.Tests;

public sealed class WorkDirectoryToolTests
{
    [Fact]
    public async Task List_and_copy_keep_template_workflow_inside_work_directory()
    {
        var workDir = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "templates"));
            await File.WriteAllTextAsync(Path.Combine(workDir, "templates", "job-advert-template.docx"), "template");

            var tool = new WorkDirectoryTool();
            var list = await tool.InvokeAsync(
                new ToolInvocation("workdir.list_files", "{\"path\":\"templates\",\"pattern\":\"*.docx\"}"),
                MakeContext(workDir));

            Assert.False(list.IsError);
            using (var doc = JsonDocument.Parse(list.Content))
            {
                var items = doc.RootElement.GetProperty("items");
                Assert.Single(items.EnumerateArray());
                Assert.Equal("templates/job-advert-template.docx", items[0].GetProperty("path").GetString());
            }

            var copy = await tool.InvokeAsync(
                new ToolInvocation("workdir.copy_file", "{\"source\":\"templates/job-advert-template.docx\",\"destination\":\"output/job-advert-01.docx\"}"),
                MakeContext(workDir));

            Assert.False(copy.IsError);
            Assert.True(File.Exists(Path.Combine(workDir, "output", "job-advert-01.docx")));
            Assert.True(File.Exists(Path.Combine(workDir, "templates", "job-advert-template.docx")));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Copy_rejects_path_traversal()
    {
        var workDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "template.docx"), "template");
            var tool = new WorkDirectoryTool();

            var result = await tool.InvokeAsync(
                new ToolInvocation("workdir.copy_file", "{\"source\":\"template.docx\",\"destination\":\"../escape.docx\"}"),
                MakeContext(workDir));

            Assert.True(result.IsError);
            Assert.Contains("work directory", result.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
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
        var path = Path.Combine(Path.GetTempPath(), "mla-workdir-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}