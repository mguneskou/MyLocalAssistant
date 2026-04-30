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

        // Cloud LLM provider keys are also owner-only. The actual key strings never leave
        // the server: GET returns booleans only; PUT writes new values (DPAPI-encrypted).
        owner.MapGet("/cloud-keys", (ServerSettings s) => Results.Ok(new CloudKeysStatusDto(
            s.IsOpenAiConfigured, s.IsAnthropicConfigured, s.OpenAiBaseUrl)));
        owner.MapPut("/cloud-keys", (UpdateCloudKeysRequest req, ServerSettings s, ServerSettingsStore store) =>
        {
            // null = leave alone; empty = clear; any other value = replace (and re-encrypt).
            if (req.OpenAiApiKey is not null)
                s.OpenAiApiKeyProtected = req.OpenAiApiKey.Length == 0
                    ? null
                    : SecretProtector.Protect(req.OpenAiApiKey.Trim());
            if (req.AnthropicApiKey is not null)
                s.AnthropicApiKeyProtected = req.AnthropicApiKey.Length == 0
                    ? null
                    : SecretProtector.Protect(req.AnthropicApiKey.Trim());
            if (req.OpenAiBaseUrl is not null)
                s.OpenAiBaseUrl = req.OpenAiBaseUrl.Length == 0 ? null : req.OpenAiBaseUrl.Trim();
            store.Save(s);
            return Results.Ok(new CloudKeysStatusDto(
                s.IsOpenAiConfigured, s.IsAnthropicConfigured, s.OpenAiBaseUrl));
        });
        owner.MapPost("/cloud-keys/test/openai", async (ServerSettings s, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var key = s.GetOpenAiApiKey();
            if (string.IsNullOrEmpty(key))
                return Results.Ok(new CloudKeyTestResultDto(false, "OpenAI API key is not configured."));
            try
            {
                using var http = hf.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                var baseUrl = string.IsNullOrWhiteSpace(s.OpenAiBaseUrl) ? "https://api.openai.com/v1" : s.OpenAiBaseUrl!.TrimEnd('/');
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                using var resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                    return Results.Ok(new CloudKeyTestResultDto(true, $"OK ({(int)resp.StatusCode})"));
                var body = await resp.Content.ReadAsStringAsync(ct);
                return Results.Ok(new CloudKeyTestResultDto(false, $"{(int)resp.StatusCode}: {Truncate(body, 300)}"));
            }
            catch (Exception ex) { return Results.Ok(new CloudKeyTestResultDto(false, ex.Message)); }
        });
        owner.MapPost("/cloud-keys/test/anthropic", async (ServerSettings s, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var key = s.GetAnthropicApiKey();
            if (string.IsNullOrEmpty(key))
                return Results.Ok(new CloudKeyTestResultDto(false, "Anthropic API key is not configured."));
            try
            {
                using var http = hf.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                // Anthropic has no /models endpoint; smallest validation is a 1-token messages call.
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new
                    {
                        model = "claude-3-5-haiku-latest",
                        max_tokens = 1,
                        messages = new object[] { new { role = "user", content = "ping" } },
                    }),
                };
                req.Headers.TryAddWithoutValidation("x-api-key", key);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                using var resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                    return Results.Ok(new CloudKeyTestResultDto(true, $"OK ({(int)resp.StatusCode})"));
                var body = await resp.Content.ReadAsStringAsync(ct);
                return Results.Ok(new CloudKeyTestResultDto(false, $"{(int)resp.StatusCode}: {Truncate(body, 300)}"));
            }
            catch (Exception ex) { return Results.Ok(new CloudKeyTestResultDto(false, ex.Message)); }
        });
        return app;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

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
