using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Download;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Server;
using MyLocalAssistant.Server.Api;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Server.ClientBridge;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;
using Serilog;

// Keep JWT claim names as-is (don't rewrite "sub" -> ClaimTypes.NameIdentifier).
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

ServerPaths.EnsureCreated();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(ServerPaths.LogsDirectory, "server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var settingsStore = new ServerSettingsStore();
    var settings = settingsStore.Load();

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = ServerPaths.AppDirectory,
    });

    builder.Host.UseSerilog();
    builder.Host.UseWindowsService(o => o.ServiceName = "MyLocalAssistantServer");
    ConfigureKestrelEndpoint(builder, settings);

    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton(settingsStore);
    builder.Services.AddSingleton<JwtIssuer>();
    builder.Services.AddSingleton<LdapIdentityProvider>();

    builder.Services.AddDbContext<AppDbContext>(o =>
        o.UseSqlite($"Data Source={ServerPaths.DatabasePath}"));
    builder.Services.AddScoped<UserService>();
    builder.Services.AddScoped<DepartmentService>();
    builder.Services.AddScoped<AgentService>();

    // LLM stack (Phase 3): single provider instance, model lifecycle, downloads.
    builder.Services.AddSingleton(sp => ModelCatalogService.LoadEmbedded());
    builder.Services.AddSingleton<ModelDownloader>(_ => new ModelDownloader());
    builder.Services.AddSingleton<DownloadCoordinator>();
    builder.Services.AddSingleton<LLamaSharpProvider>();
    builder.Services.AddSingleton<EmbeddingService>();
    // Chat providers (v2.3): one per ModelSource. Router picks based on the active catalog entry.
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.IChatProvider, MyLocalAssistant.Server.Llm.LocalChatProvider>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.IChatProvider, MyLocalAssistant.Server.Llm.OpenAiChatProvider>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.IChatProvider, MyLocalAssistant.Server.Llm.AnthropicChatProvider>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.IChatProvider, MyLocalAssistant.Server.Llm.GroqChatProvider>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.IChatProvider, MyLocalAssistant.Server.Llm.GeminiChatProvider>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.IChatProvider, MyLocalAssistant.Server.Llm.MistralChatProvider>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.IChatProvider, MyLocalAssistant.Server.Llm.CerebrasChatProvider>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.ChatProviderRouter>();
    builder.Services.AddSingleton<ModelManager>();
    builder.Services.AddSingleton<InferenceQueue>();
    builder.Services.AddSingleton<AuditWriter>();
    builder.Services.AddSingleton<IVectorStore, MyLocalAssistant.Server.Rag.LanceDbVectorStore>();
    builder.Services.AddScoped<MyLocalAssistant.Server.Rag.IngestionService>();
    builder.Services.AddScoped<MyLocalAssistant.Server.Rag.RagAuthorizationService>();
    builder.Services.AddScoped<MyLocalAssistant.Server.Rag.RagService>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Llm.ModelCapabilityRegistry>();
    builder.Services.AddScoped<ChatService>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.ClientBridge.ClientBridgeHub>();

    // Tools (Phase 1): in-process built-ins. Plug-in discovery comes in Phase 3.
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.MathEvalTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.TimeNowTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.RagSearchTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.ClientFsTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.WebSearchTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.CodeInterpreterTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.ImageGenTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.MemoryTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.ReportGenTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.ExcelTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.WordTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.PowerPointTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.PdfTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.EmailTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ITool, MyLocalAssistant.Server.Tools.BuiltIn.SchedulerTool>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ToolRegistry>();
    builder.Services.AddSingleton<MyLocalAssistant.Server.Tools.ToolCallStats>();
    builder.Services.AddHostedService<ModelBootstrapService>();
    builder.Services.AddHostedService<EmbeddingBootstrapService>();
    builder.Services.AddHostedService<MyLocalAssistant.Server.Hosting.RetentionService>();
    builder.Services.AddHostedService<MyLocalAssistant.Server.Hosting.SchedulerHostedService>();
    builder.Services.AddHostedService<MyLocalAssistant.Server.Hosting.MemorySummarizationService>();

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.RequireHttpsMetadata = false; // LAN deployment, optional TLS
            o.MapInboundClaims = false;     // keep "sub" as "sub" (don't rewrite to NameIdentifier)
            o.TokenValidationParameters = new JwtIssuer(settings).GetValidationParameters();
        });
    builder.Services.AddAuthorization(o =>
    {
        o.AddPolicy("Admin", p => p.RequireClaim(JwtIssuer.ClaimIsAdmin, "1"));
        o.AddPolicy("GlobalAdmin", p => p.RequireClaim(JwtIssuer.ClaimIsGlobalAdmin, "1"));
    });

    builder.Services.AddProblemDetails();

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Phase 2.1 schema change: User.Department string -> Department/UserDepartment tables.
        // We use EnsureCreatedAsync (no migrations); if the legacy column exists, recreate the DB.
        if (await IsLegacySchemaAsync(db))
        {
            Log.Warning("Legacy schema detected (User.Department column). Resetting database at {Path}.", ServerPaths.DatabasePath);
            await db.Database.EnsureDeletedAsync();
        }
        await db.Database.EnsureCreatedAsync();
        // Phase 14: User.WorkRoot column added (per-user filesystem scratch root). Additive
        // change - do an in-place ALTER instead of resetting the DB so existing user accounts,
        // password hashes, agents and conversations all survive the upgrade.
        await EnsureUserWorkRootColumnAsync(db);
        // Skills→Tools rename (v2.4.0): Agent.SkillIds renamed to Agent.ToolIds.
        await EnsureAgentToolIdsColumnAsync(db);
        // v2.8.0: AuditEntries.IsAdminAction column added.
        await EnsureAuditIsAdminActionColumnAsync(db);
        // v2.19.0: Agent.ScenarioNotes column added.
        await EnsureAgentScenarioNotesColumnAsync(db);
        var userSvc = scope.ServiceProvider.GetRequiredService<UserService>();
        await userSvc.EnsureAdminBootstrapAsync();
        await userSvc.EnsureGlobalAdminAsync();
        var deptSvc = scope.ServiceProvider.GetRequiredService<DepartmentService>();
        await deptSvc.SeedAsync();
        var agentSvc = scope.ServiceProvider.GetRequiredService<AgentService>();
        await agentSvc.SeedAsync();
        var toolRegistry = app.Services.GetRequiredService<MyLocalAssistant.Server.Tools.ToolRegistry>();
        await toolRegistry.SeedAsync();
    }

    app.UseSerilogRequestLogging();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
    app.UseDefaultFiles();  // serves index.html for /
    app.UseStaticFiles();   // serves wwwroot (the React SPA build output)
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthEndpoints();
    app.MapAuthEndpoints();
    app.MapUserAdminEndpoints();
    app.MapDepartmentEndpoints();
    app.MapAgentEndpoints();
    app.MapModelEndpoints();
    app.MapRagEndpoints();
    app.MapChatEndpoints();
    app.MapConversationEndpoints();
    app.MapRoleEndpoints();
    app.MapAuditEndpoints();
    app.MapStatsEndpoints();
    app.MapSettingsEndpoints();
    app.MapAttachmentEndpoints();
    app.MapToolEndpoints();
    app.MapClientBridgeEndpoints();
    // SPA fallback: any path not matched by an API route returns index.html so
    // React Router handles client-side navigation.
    app.MapFallbackToFile("index.html");
    Log.Information("MyLocalAssistant.Server starting. Listening on {Url}. AppDir={Dir}",
        settings.ListenUrl, ServerPaths.AppDirectory);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Server terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static void ConfigureKestrelEndpoint(WebApplicationBuilder builder, ServerSettings settings)
{
    if (!Uri.TryCreate(settings.ListenUrl, UriKind.Absolute, out var uri))
        throw new InvalidOperationException($"Invalid ListenUrl '{settings.ListenUrl}'.");

    var isHttps = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
    if (!isHttps)
    {
        builder.WebHost.UseUrls(settings.ListenUrl);
        return;
    }

    if (string.IsNullOrWhiteSpace(settings.CertificatePath))
        throw new InvalidOperationException("HTTPS ListenUrl requires CertificatePath in server.json.");
    if (!File.Exists(settings.CertificatePath))
        throw new FileNotFoundException("TLS certificate not found.", settings.CertificatePath);

    var bindHost = uri.Host;
    var bindAddress = bindHost switch
    {
        "0.0.0.0" or "*" or "+" => System.Net.IPAddress.Any,
        "[::]" => System.Net.IPAddress.IPv6Any,
        _ => System.Net.IPAddress.TryParse(bindHost, out var ip) ? ip : null,
    };

    var certPath = settings.CertificatePath;
    var certPwd = settings.CertificatePassword ?? "";
    builder.WebHost.ConfigureKestrel(k =>
    {
        void Configure(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions o)
            => o.UseHttps(certPath, certPwd);
        if (bindAddress is not null)
            k.Listen(bindAddress, uri.Port, Configure);
        else
            k.ListenAnyIP(uri.Port, Configure); // fall back; hostname binding is unusual on Kestrel
    });
}

static async Task<bool> IsLegacySchemaAsync(AppDbContext db)
{
    if (!File.Exists(ServerPaths.DatabasePath)) return false;
    try
    {
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        // Legacy: pre-multi-dept User.Department column.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT 1 FROM pragma_table_info('Users') WHERE name = 'Department' LIMIT 1";
            if (await cmd.ExecuteScalarAsync() is not null) return true;
        }
        // Legacy: pre-Phase-4 AgentAclRules table (replaced by Agent.IsGeneric + dept-name match).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='AgentAclRules' LIMIT 1";
            if (await cmd.ExecuteScalarAsync() is not null) return true;
        }
        // Phase 5a: Agent.RagCollectionIds column added.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT 1 FROM pragma_table_info('Agents') WHERE name = 'RagCollectionIds' LIMIT 1";
            if (await cmd.ExecuteScalarAsync() is null) return true;
        }
        // Phase 11: User.AuthSource column added (LDAP support).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT 1 FROM pragma_table_info('Users') WHERE name = 'AuthSource' LIMIT 1";
            if (await cmd.ExecuteScalarAsync() is null) return true;
        }
        // Phase 13: User.IsGlobalAdmin column added (global admin tier).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT 1 FROM pragma_table_info('Users') WHERE name = 'IsGlobalAdmin' LIMIT 1";
            if (await cmd.ExecuteScalarAsync() is null) return true;
        }
        // v2.4.0: SkillIds renamed to ToolIds — handled by EnsureAgentToolIdsColumnAsync,
        // NOT a full reset, so explicitly exclude it from the legacy-schema check.
        return false;
    }
    catch
    {
        return false;
    }
}

static async Task EnsureUserWorkRootColumnAsync(AppDbContext db)
{
    try
    {
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pragma_table_info('Users') WHERE name = 'WorkRoot' LIMIT 1";
            if (await probe.ExecuteScalarAsync() is not null) return;
        }
        await using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE Users ADD COLUMN WorkRoot TEXT NULL";
        await alter.ExecuteNonQueryAsync();
        Log.Information("Added User.WorkRoot column to existing database.");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not add WorkRoot column to Users; per-user work directories will be unavailable until restart.");
    }
}

static async Task EnsureAgentToolIdsColumnAsync(AppDbContext db)
{
    // v2.4.0: Agent.SkillIds was renamed to Agent.ToolIds. SQLite supports
    // RENAME COLUMN since 3.25.0 (bundled Microsoft.Data.Sqlite is always recent enough).
    try
    {
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        // Check whether the old column name still exists.
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pragma_table_info('Agents') WHERE name = 'SkillIds' LIMIT 1";
            if (await probe.ExecuteScalarAsync() is null) return; // already up to date
        }
        await using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE Agents RENAME COLUMN SkillIds TO ToolIds";
        await alter.ExecuteNonQueryAsync();
        Log.Information("Renamed Agents.SkillIds -> ToolIds in existing database.");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not rename SkillIds to ToolIds in Agents table.");
    }
}

static async Task EnsureAuditIsAdminActionColumnAsync(AppDbContext db)
{
    try
    {
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pragma_table_info('AuditEntries') WHERE name = 'IsAdminAction' LIMIT 1";
            if (await probe.ExecuteScalarAsync() is not null) return;
        }
        await using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE AuditEntries ADD COLUMN IsAdminAction INTEGER NOT NULL DEFAULT 0";
        await alter.ExecuteNonQueryAsync();
        Log.Information("Added AuditEntries.IsAdminAction column to existing database.");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not add IsAdminAction column to AuditEntries.");
    }
}

static async Task EnsureAgentScenarioNotesColumnAsync(AppDbContext db)
{
    try
    {
        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pragma_table_info('Agents') WHERE name = 'ScenarioNotes' LIMIT 1";
            if (await probe.ExecuteScalarAsync() is not null) return;
        }
        await using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE Agents ADD COLUMN ScenarioNotes TEXT NULL";
        await alter.ExecuteNonQueryAsync();
        Log.Information("Added Agents.ScenarioNotes column to existing database.");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not add ScenarioNotes column to Agents.");
    }
}
