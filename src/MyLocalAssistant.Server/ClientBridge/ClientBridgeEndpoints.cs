using System.Security.Claims;
using MyLocalAssistant.Server.Auth;

namespace MyLocalAssistant.Server.ClientBridge;

public static class ClientBridgeEndpoints
{
    public static IEndpointRouteBuilder MapClientBridgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/client/bridge", HandleAsync)
            .WithTags("ClientBridge")
            .RequireAuthorization();
        return app;
    }

    private static async Task HandleAsync(HttpContext http, ClientBridgeHub hub, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Expected WebSocket upgrade.", ct);
            return;
        }

        var sub = http.User.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var userId))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var log = loggerFactory.CreateLogger("ClientBridge");
        using var ws = await http.WebSockets.AcceptWebSocketAsync();
        log.LogInformation("Client bridge connected for user {UserId} from {Ip}.", userId, http.Connection.RemoteIpAddress);
        try
        {
            await hub.RunWebSocketAsync(ws, userId, log, ct);
        }
        finally
        {
            log.LogInformation("Client bridge closed for user {UserId}.", userId);
        }
    }
}
