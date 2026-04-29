using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Server.Llm;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class ChatEndpoints
{
    private const int DefaultMaxTokens = 512;
    private const int MaxAllowedTokens = 4096;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/stream", HandleStreamAsync)
            .WithTags("Chat")
            .RequireAuthorization();
        return app;
    }

    private static async Task HandleStreamAsync(
        HttpContext http,
        ChatRequest req,
        ChatService chat,
        AuditWriter audit,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Chat");
        var user = http.User;
        var sub = user.FindFirstValue("sub");
        Guid.TryParse(sub, out var userId);
        var username = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name);
        var isAdmin = user.HasClaim(JwtIssuer.ClaimIsAdmin, "1");
        var ip = http.Connection.RemoteIpAddress?.ToString();

        if (string.IsNullOrWhiteSpace(req.AgentId) || string.IsNullOrWhiteSpace(req.Message))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { detail = "agentId and message are required." }, ct);
            return;
        }

        var check = await chat.CheckVisibilityAsync(req.AgentId, userId, isAdmin, ct);
        if (!check.Allowed || check.Agent is null)
        {
            await audit.WriteAsync("chat.denied", userId, username, success: false,
                agentId: req.AgentId, detail: check.Reason, ipAddress: ip, ct: ct);
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { detail = check.Reason ?? "Not allowed." }, ct);
            return;
        }

        var maxTokens = Math.Clamp(req.MaxTokens ?? DefaultMaxTokens, 1, MaxAllowedTokens);

        // Open the SSE response.
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.Body.FlushAsync(ct);

        var sw = Stopwatch.StartNew();
        var totalChars = 0;
        var tokenCount = 0;
        string? error = null;
        try
        {
            await foreach (var token in chat.StreamAsync(check.Agent, req.Message, maxTokens, ct))
            {
                tokenCount++;
                totalChars += token.Length;
                await WriteFrameAsync(http.Response, new TokenStreamFrame(TokenStreamFrameKind.Token, Text: token), ct);
            }
            await WriteFrameAsync(http.Response, new TokenStreamFrame(TokenStreamFrameKind.End), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error = "Cancelled by client.";
            log.LogInformation("Chat cancelled by client (agent={Agent}).", req.AgentId);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.LogError(ex, "Chat generation failed (agent={Agent}).", req.AgentId);
            try
            {
                await WriteFrameAsync(http.Response, new TokenStreamFrame(TokenStreamFrameKind.Error, ErrorMessage: ex.Message), ct);
            }
            catch { /* response already broken */ }
        }
        finally
        {
            sw.Stop();
            var detail = $"tokens={tokenCount}; chars={totalChars}; ms={sw.ElapsedMilliseconds}";
            if (error is not null) detail += $"; error={error}";
            await audit.WriteAsync(
                action: error is null ? "chat.send" : "chat.error",
                userId: userId,
                username: username,
                success: error is null,
                agentId: req.AgentId,
                detail: detail,
                ipAddress: ip,
                ct: CancellationToken.None);
        }
    }

    private static async Task WriteFrameAsync(HttpResponse resp, TokenStreamFrame frame, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(frame, s_json);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await resp.Body.WriteAsync(bytes, ct);
        await resp.Body.FlushAsync(ct);
    }
}
