using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Core.Catalog;
using MyLocalAssistant.Core.Download;
using MyLocalAssistant.Core.Inference;
using MyLocalAssistant.Server;
using MyLocalAssistant.Server.Api;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Server.Persistence;
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
    builder.WebHost.UseUrls(settings.ListenUrl);

    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton(settingsStore);
    builder.Services.AddSingleton<JwtIssuer>();

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
    builder.Services.AddSingleton<ModelManager>();
    builder.Services.AddSingleton<InferenceQueue>();
    builder.Services.AddSingleton<AuditWriter>();
    builder.Services.AddScoped<ChatService>();
    builder.Services.AddHostedService<ModelBootstrapService>();
    builder.Services.AddHostedService<EmbeddingBootstrapService>();

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
        var userSvc = scope.ServiceProvider.GetRequiredService<UserService>();
        await userSvc.EnsureAdminBootstrapAsync();
        var deptSvc = scope.ServiceProvider.GetRequiredService<DepartmentService>();
        await deptSvc.SeedAsync();
        var agentSvc = scope.ServiceProvider.GetRequiredService<AgentService>();
        await agentSvc.SeedAsync();
    }

    app.UseSerilogRequestLogging();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthEndpoints();
    app.MapAuthEndpoints();
    app.MapUserAdminEndpoints();
    app.MapDepartmentEndpoints();
    app.MapAgentEndpoints();
    app.MapModelEndpoints();
    app.MapChatEndpoints();

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
        return false;
    }
    catch
    {
        return false;
    }
}
