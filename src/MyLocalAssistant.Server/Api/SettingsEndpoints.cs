using MyLocalAssistant.Server.Configuration;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class SettingsEndpoints
{
    /// <summary>Cap on global system prompt length: 8 KB (~2k tokens).</summary>
    public const int MaxGlobalPromptBytes = 8 * 1024;

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/settings").WithTags("Settings").RequireAuthorization("Admin");
        g.MapGet("/", Get);
        g.MapPatch("/", Patch);

        // Global system prompt is owner-only on both read and write.
        var owner = app.MapGroup("/api/admin/settings").WithTags("Settings").RequireAuthorization("GlobalAdmin");
        owner.MapGet("/global-prompt", (ServerSettings s) => Results.Ok(new GlobalSystemPromptDto(s.GlobalSystemPrompt ?? "")));
        owner.MapPut("/global-prompt", (UpdateGlobalSystemPromptRequest req, ServerSettings s, ServerSettingsStore store) =>
        {
            var text = req.SystemPrompt ?? "";
            if (text.Length > MaxGlobalPromptBytes)
                return Results.BadRequest(new { detail = $"SystemPrompt exceeds {MaxGlobalPromptBytes} chars." });
            s.GlobalSystemPrompt = string.IsNullOrWhiteSpace(text) ? null : text;
            store.Save(s);
            return Results.Ok(new GlobalSystemPromptDto(s.GlobalSystemPrompt ?? ""));
        });
        return app;
    }

    private static IResult Get(ServerSettings s) => Results.Ok(ToDto(s));

    private static IResult Patch(UpdateServerSettingsRequest req, ServerSettings s, ServerSettingsStore store)
    {
        if (req.AccessTokenMinutes is < 1 or > 24 * 60)
            return Results.BadRequest(new { detail = "AccessTokenMinutes must be 1..1440." });
        if (req.RefreshTokenDays is < 1 or > 365)
            return Results.BadRequest(new { detail = "RefreshTokenDays must be 1..365." });
        if (req.MessageBodyRetentionDays is < 1 or > 3650)
            return Results.BadRequest(new { detail = "MessageBodyRetentionDays must be 1..3650." });
        if (req.AuditRetentionDays is < 1 or > 3650)
            return Results.BadRequest(new { detail = "AuditRetentionDays must be 1..3650." });

        // ServerSettings is registered as a singleton; mutate in place so other singletons
        // (JwtIssuer, RetentionService) observe the new values without a restart.
        s.AccessTokenMinutes = req.AccessTokenMinutes;
        s.RefreshTokenDays = req.RefreshTokenDays;
        s.MessageBodyRetentionDays = req.MessageBodyRetentionDays;
        s.AuditRetentionDays = req.AuditRetentionDays;
        store.Save(s);
        return Results.Ok(ToDto(s));
    }

    private static ServerSettingsDto ToDto(ServerSettings s) => new(
        s.ListenUrl, s.JwtIssuer, s.JwtAudience,
        s.AccessTokenMinutes, s.RefreshTokenDays,
        s.MessageBodyRetentionDays, s.AuditRetentionDays,
        s.DefaultModelId, s.EmbeddingModelId);
}
