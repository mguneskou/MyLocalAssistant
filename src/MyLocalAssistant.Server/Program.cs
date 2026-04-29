using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server;
using MyLocalAssistant.Server.Api;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Server.Configuration;
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
        await db.Database.EnsureCreatedAsync();
        var userSvc = scope.ServiceProvider.GetRequiredService<UserService>();
        await userSvc.EnsureAdminBootstrapAsync();
    }

    app.UseSerilogRequestLogging();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthEndpoints();
    app.MapAuthEndpoints();

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
