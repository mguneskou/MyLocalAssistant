using MyLocalAssistant.Server.Tools.BuiltIn;

namespace MyLocalAssistant.Core.Tests;

public sealed class SqlServerToolTests
{
    [Theory]
    [InlineData("UPDATE dbo.Users SET Name = 'x'")]
    [InlineData("WITH cte AS (SELECT 1 AS Id) DELETE FROM dbo.Users WHERE Id IN (SELECT Id FROM cte)")]
    [InlineData("SELECT * INTO dbo.UsersBackup FROM dbo.Users")]
    public void Read_only_validator_rejects_write_or_ddl_sql(string sql)
    {
        var ok = SqlServerTool.TryValidateReadOnlySql(sql, out var error);

        Assert.False(ok);
        Assert.Contains("read-only", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_only_validator_ignores_keywords_inside_comments_and_literals()
    {
        const string sql = "-- delete later\nSELECT 'update', [drop] AS label";

        var ok = SqlServerTool.TryValidateReadOnlySql(sql, out var error);

        Assert.True(ok, error);
        Assert.Equal(string.Empty, error);
    }
}