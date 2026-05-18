using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

internal sealed class SqlServerTool : ITool
{
    public string Id => "sqlserver";
    public string Name => "SQL Server Tool";
    public string Description => "Read-only Microsoft SQL Server access for repeatable office workflows. Use connection files and SQL files from the work directory when available, then move the results into Word, Excel, or PowerPoint deliverables.";
    public string Category => "Data";
    public string Source => ToolSources.BuiltIn;
    public string? Version => null;
    public string? Publisher => "MyLocalAssistant";
    public string? KeyId => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "sqlserver.list_tables",
            Description: "List tables and optional views available in a SQL Server database. Accepts either a raw connection string or a connection file stored in the work directory.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"connectionString":{"type":"string","description":"Optional SQL Server connection string."},"connectionFile":{"type":"string","description":"Optional relative path in the work directory to a connection profile or plain-text connection string."},"schema":{"type":"string","description":"Optional schema name such as dbo."},"namePattern":{"type":"string","description":"Optional LIKE pattern such as Sales% or %Invoice%."},"includeViews":{"type":"boolean","description":"Include views as well as base tables. Default false."},"timeoutSeconds":{"type":"integer","description":"Command timeout in seconds (default 30, max 300)."}},"additionalProperties":false}"""),
        new ToolFunctionDto(
            Name: "sqlserver.describe_table",
            Description: "Describe a table or view in SQL Server, including column names, types, nullability, length, numeric precision, and primary-key columns.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"connectionString":{"type":"string"},"connectionFile":{"type":"string"},"schema":{"type":"string","description":"Optional schema. If omitted, all matching schemas are checked."},"table":{"type":"string","description":"Table or view name."},"timeoutSeconds":{"type":"integer","description":"Command timeout in seconds (default 30, max 300)."}},"required":["table"],"additionalProperties":false}"""),
        new ToolFunctionDto(
            Name: "sqlserver.query",
            Description: "Execute a single read-only SQL Server SELECT query and return structured rows. Accepts inline SQL or a .sql file from the work directory. This tool rejects write, DDL, admin, and multi-statement SQL.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"connectionString":{"type":"string"},"connectionFile":{"type":"string"},"sql":{"type":"string","description":"Inline SQL SELECT statement."},"sqlFile":{"type":"string","description":"Relative path in the work directory to a .sql text file."},"maxRows":{"type":"integer","description":"Maximum rows to return (default 200, max 2000)."},"timeoutSeconds":{"type":"integer","description":"Command timeout in seconds (default 30, max 300)."}},"additionalProperties":false}"""),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Tags, MinContextK: 8);

    private int _defaultTimeoutSeconds = 30;
    private int _defaultMaxRows = 200;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (cfg?.DefaultTimeoutSeconds is > 0 and <= 300) _defaultTimeoutSeconds = cfg.DefaultTimeoutSeconds.Value;
        if (cfg?.DefaultMaxRows is > 0 and <= 2000) _defaultMaxRows = cfg.DefaultMaxRows.Value;
    }

    public async Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args = doc.RootElement.Clone();

        try
        {
            return call.ToolName switch
            {
                "sqlserver.list_tables" => await ListTablesAsync(args, ctx),
                "sqlserver.describe_table" => await DescribeTableAsync(args, ctx),
                "sqlserver.query" => await QueryAsync(args, ctx),
                _ => ToolResult.Error($"Unknown tool '{call.ToolName}'"),
            };
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (SqlException ex)
        {
            return ToolResult.Error($"SQL Server error: {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolResult.Error($"I/O error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return ToolResult.Error($"Invalid JSON: {ex.Message}");
        }
    }

    private async Task<ToolResult> ListTablesAsync(JsonElement args, ToolContext ctx)
    {
        var connectionString = ResolveConnectionString(args, ctx);
        var schema = GetString(args, "schema");
        var namePattern = GetString(args, "namePattern");
        var includeViews = GetBool(args, "includeViews");
        var timeout = GetTimeoutSeconds(args);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ctx.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = timeout;
        cmd.CommandText = @"
SELECT TABLE_SCHEMA AS [schema], TABLE_NAME AS [name], TABLE_TYPE AS [type]
FROM INFORMATION_SCHEMA.TABLES
WHERE (@schema IS NULL OR TABLE_SCHEMA = @schema)
  AND (@namePattern IS NULL OR TABLE_NAME LIKE @namePattern)
  AND (@includeViews = 1 OR TABLE_TYPE = 'BASE TABLE')
ORDER BY TABLE_SCHEMA, TABLE_NAME;";
        cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = (object?)schema ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@namePattern", SqlDbType.NVarChar, 256) { Value = (object?)namePattern ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@includeViews", SqlDbType.Bit) { Value = includeViews });

        var items = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ctx.CancellationToken);
        while (await reader.ReadAsync(ctx.CancellationToken))
        {
            items.Add(new
            {
                schema = reader.GetString(0),
                name = reader.GetString(1),
                type = reader.GetString(2),
            });
        }

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            database = conn.Database,
            items,
        }, s_json));
    }

    private async Task<ToolResult> DescribeTableAsync(JsonElement args, ToolContext ctx)
    {
        var connectionString = ResolveConnectionString(args, ctx);
        var table = GetRequiredString(args, "table");
        var schema = GetString(args, "schema");
        var timeout = GetTimeoutSeconds(args);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ctx.CancellationToken);

        var columns = new List<object>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = timeout;
            cmd.CommandText = @"
SELECT
    c.TABLE_SCHEMA,
    c.TABLE_NAME,
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.IS_NULLABLE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.NUMERIC_PRECISION,
    c.NUMERIC_SCALE,
    COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity') AS IS_IDENTITY
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_NAME = @table
  AND (@schema IS NULL OR c.TABLE_SCHEMA = @schema)
ORDER BY c.TABLE_SCHEMA, c.ORDINAL_POSITION;";
            cmd.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = table });
            cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = (object?)schema ?? DBNull.Value });

            await using var reader = await cmd.ExecuteReaderAsync(ctx.CancellationToken);
            while (await reader.ReadAsync(ctx.CancellationToken))
            {
                columns.Add(new
                {
                    schema = reader.GetString(0),
                    table = reader.GetString(1),
                    name = reader.GetString(2),
                    dataType = reader.GetString(3),
                    isNullable = string.Equals(reader.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                    maxLength = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5),
                    precision = reader.IsDBNull(6) ? (byte?)null : reader.GetByte(6),
                    scale = reader.IsDBNull(7) ? (int?)null : Convert.ToInt32(reader.GetValue(7)),
                    isIdentity = !reader.IsDBNull(8) && Convert.ToInt32(reader.GetValue(8)) == 1,
                });
            }
        }

        if (columns.Count == 0) return ToolResult.Error($"Table or view not found: {table}");

        var primaryKeys = new List<object>();
        await using (var pkCmd = conn.CreateCommand())
        {
            pkCmd.CommandTimeout = timeout;
            pkCmd.CommandText = @"
SELECT kcu.TABLE_SCHEMA, kcu.TABLE_NAME, kcu.COLUMN_NAME
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
  ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
 AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
  AND kcu.TABLE_NAME = @table
  AND (@schema IS NULL OR kcu.TABLE_SCHEMA = @schema)
ORDER BY kcu.TABLE_SCHEMA, kcu.ORDINAL_POSITION;";
            pkCmd.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = table });
            pkCmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = (object?)schema ?? DBNull.Value });

            await using var reader = await pkCmd.ExecuteReaderAsync(ctx.CancellationToken);
            while (await reader.ReadAsync(ctx.CancellationToken))
            {
                primaryKeys.Add(new
                {
                    schema = reader.GetString(0),
                    table = reader.GetString(1),
                    name = reader.GetString(2),
                });
            }
        }

        var firstColumn = columns[0];
        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            database = conn.Database,
            table,
            schema,
            columns,
            primaryKeys,
        }, s_json));
    }

    private async Task<ToolResult> QueryAsync(JsonElement args, ToolContext ctx)
    {
        var sql = ResolveSql(args, ctx);
        if (!TryValidateReadOnlySql(sql, out var validationError))
            return ToolResult.Error(validationError);

        var connectionString = ResolveConnectionString(args, ctx);
        var maxRows = Math.Clamp(GetInt(args, "maxRows") ?? _defaultMaxRows, 1, 2000);
        var timeout = GetTimeoutSeconds(args);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ctx.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = timeout;
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(ctx.CancellationToken);
        var columnNames = BuildColumnNames(reader);
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => new { name = columnNames[i], dataType = reader.GetDataTypeName(i) })
            .ToList();

        var rows = new List<Dictionary<string, object?>>(Math.Min(maxRows, 256));
        var truncated = false;
        while (await reader.ReadAsync(ctx.CancellationToken))
        {
            if (rows.Count == maxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[columnNames[i]] = NormalizeValue(reader.GetValue(i));
            }
            rows.Add(row);
        }

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            database = conn.Database,
            maxRows,
            rowCount = rows.Count,
            truncated,
            columns,
            rows,
        }, s_json));
    }

    internal static bool TryValidateReadOnlySql(string sql, out string error)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            error = "sql or sqlFile is required.";
            return false;
        }

        var normalized = StripCommentsAndQuotedText(sql).Trim();
        if (normalized.Length == 0)
        {
            error = "sql or sqlFile is required.";
            return false;
        }

        if (normalized.EndsWith(';'))
            normalized = normalized[..^1].TrimEnd();
        if (normalized.Contains(';'))
        {
            error = "Only a single read-only SQL statement is allowed.";
            return false;
        }

        if (!normalized.StartsWith("select", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("with", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only read-only SELECT statements are allowed.";
            return false;
        }

        foreach (var keyword in s_forbiddenKeywords)
        {
            if (Regex.IsMatch(normalized, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                error = $"Only read-only SQL is allowed. Forbidden keyword: {keyword}.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static readonly string[] s_forbiddenKeywords =
    [
        "insert",
        "update",
        "delete",
        "merge",
        "drop",
        "alter",
        "create",
        "truncate",
        "exec",
        "execute",
        "grant",
        "revoke",
        "deny",
        "backup",
        "restore",
        "dbcc",
        "use",
        "into",
    ];

    private static string ResolveConnectionString(JsonElement args, ToolContext ctx)
    {
        var direct = GetString(args, "connectionString");
        if (!string.IsNullOrWhiteSpace(direct)) return direct!;

        var connectionFile = GetString(args, "connectionFile");
        if (string.IsNullOrWhiteSpace(connectionFile))
            throw new ArgumentException("connectionString or connectionFile is required.");

        var path = ResolveWorkDirectoryFile(ctx.WorkDirectory, connectionFile!);
        if (!File.Exists(path)) throw new ArgumentException($"Connection file not found: {connectionFile}");

        var raw = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException($"Connection file is empty: {connectionFile}");
        if (!raw.StartsWith('{')) return raw;

        var profile = JsonSerializer.Deserialize<ConnectionProfile>(raw, s_json)
            ?? throw new ArgumentException($"Connection profile is invalid: {connectionFile}");
        if (!string.IsNullOrWhiteSpace(profile.ConnectionString)) return profile.ConnectionString!;

        var builder = new SqlConnectionStringBuilder();
        var server = profile.Server ?? profile.DataSource;
        if (string.IsNullOrWhiteSpace(server))
            throw new ArgumentException($"Connection profile must contain 'connectionString' or 'server': {connectionFile}");

        builder.DataSource = server;
        if (!string.IsNullOrWhiteSpace(profile.Database ?? profile.InitialCatalog))
            builder.InitialCatalog = profile.Database ?? profile.InitialCatalog;
        if (profile.IntegratedSecurity.GetValueOrDefault())
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(profile.UserId ?? profile.Username ?? profile.User))
                builder.UserID = profile.UserId ?? profile.Username ?? profile.User;
            if (!string.IsNullOrWhiteSpace(profile.Password))
                builder.Password = profile.Password;
        }
        if (profile.Encrypt.HasValue) builder.Encrypt = profile.Encrypt.Value;
        if (profile.TrustServerCertificate.HasValue) builder.TrustServerCertificate = profile.TrustServerCertificate.Value;
        if (profile.ConnectTimeout is > 0 and <= 300) builder.ConnectTimeout = profile.ConnectTimeout.Value;
        if (!string.IsNullOrWhiteSpace(profile.ApplicationName)) builder.ApplicationName = profile.ApplicationName;
        return builder.ConnectionString;
    }

    private static string ResolveSql(JsonElement args, ToolContext ctx)
    {
        var inlineSql = GetString(args, "sql");
        if (!string.IsNullOrWhiteSpace(inlineSql)) return inlineSql!;

        var sqlFile = GetString(args, "sqlFile");
        if (string.IsNullOrWhiteSpace(sqlFile))
            throw new ArgumentException("sql or sqlFile is required.");

        var path = ResolveWorkDirectoryFile(ctx.WorkDirectory, sqlFile!);
        if (!File.Exists(path)) throw new ArgumentException($"SQL file not found: {sqlFile}");
        return File.ReadAllText(path);
    }

    private int GetTimeoutSeconds(JsonElement args)
        => Math.Clamp(GetInt(args, "timeoutSeconds") ?? _defaultTimeoutSeconds, 1, 300);

    private static string ResolveWorkDirectoryFile(string workDirectory, string relativePath)
    {
        var root = Path.GetFullPath(workDirectory);
        Directory.CreateDirectory(root);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (string.Equals(root, fullPath, StringComparison.OrdinalIgnoreCase)) return fullPath;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path must stay within the work directory.");
        return fullPath;
    }

    private static string[] BuildColumnNames(SqlDataReader reader)
    {
        var result = new string[reader.FieldCount];
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrWhiteSpace(name)) name = $"Column{i + 1}";
            if (seen.TryGetValue(name, out var count))
            {
                count++;
                seen[name] = count;
                name = $"{name}_{count}";
            }
            else
            {
                seen[name] = 1;
            }
            result[i] = name;
        }
        return result;
    }

    private static object? NormalizeValue(object value)
        => value switch
        {
            DBNull => null,
            byte[] bytes => Convert.ToBase64String(bytes),
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            TimeSpan ts => ts.ToString(),
            Guid guid => guid.ToString(),
            char ch => ch.ToString(),
            _ => value,
        };

    private static string StripCommentsAndQuotedText(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBracketIdentifier = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var current = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\r' || current == '\n')
                {
                    inLineComment = false;
                    sb.Append(current);
                }
                else
                {
                    sb.Append(' ');
                }
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    sb.Append("  ");
                    i++;
                }
                else
                {
                    sb.Append(current == '\r' || current == '\n' ? current : ' ');
                }
                continue;
            }

            if (inSingleQuote)
            {
                if (current == '\'' && next == '\'')
                {
                    sb.Append("  ");
                    i++;
                    continue;
                }

                if (current == '\'') inSingleQuote = false;
                sb.Append(current == '\r' || current == '\n' ? current : ' ');
                continue;
            }

            if (inDoubleQuote)
            {
                if (current == '"' && next == '"')
                {
                    sb.Append("  ");
                    i++;
                    continue;
                }

                if (current == '"') inDoubleQuote = false;
                sb.Append(current == '\r' || current == '\n' ? current : ' ');
                continue;
            }

            if (inBracketIdentifier)
            {
                if (current == ']') inBracketIdentifier = false;
                sb.Append(current == '\r' || current == '\n' ? current : ' ');
                continue;
            }

            if (current == '-' && next == '-')
            {
                inLineComment = true;
                sb.Append("  ");
                i++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                sb.Append("  ");
                i++;
                continue;
            }

            if (current == '\'')
            {
                inSingleQuote = true;
                sb.Append(' ');
                continue;
            }

            if (current == '"')
            {
                inDoubleQuote = true;
                sb.Append(' ');
                continue;
            }

            if (current == '[')
            {
                inBracketIdentifier = true;
                sb.Append(' ');
                continue;
            }

            sb.Append(current);
        }

        return sb.ToString();
    }

    private static string GetRequiredString(JsonElement args, string name)
        => GetString(args, name) ?? throw new ArgumentException($"{name} is required.");

    private static string? GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? GetInt(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private sealed class Config
    {
        [JsonPropertyName("defaultTimeoutSeconds")] public int? DefaultTimeoutSeconds { get; set; }
        [JsonPropertyName("defaultMaxRows")] public int? DefaultMaxRows { get; set; }
    }

    private sealed class ConnectionProfile
    {
        [JsonPropertyName("connectionString")] public string? ConnectionString { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
        [JsonPropertyName("dataSource")] public string? DataSource { get; set; }
        [JsonPropertyName("database")] public string? Database { get; set; }
        [JsonPropertyName("initialCatalog")] public string? InitialCatalog { get; set; }
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("user")] public string? User { get; set; }
        [JsonPropertyName("password")] public string? Password { get; set; }
        [JsonPropertyName("integratedSecurity")] public bool? IntegratedSecurity { get; set; }
        [JsonPropertyName("encrypt")] public bool? Encrypt { get; set; }
        [JsonPropertyName("trustServerCertificate")] public bool? TrustServerCertificate { get; set; }
        [JsonPropertyName("connectTimeout")] public int? ConnectTimeout { get; set; }
        [JsonPropertyName("applicationName")] public string? ApplicationName { get; set; }
    }
}