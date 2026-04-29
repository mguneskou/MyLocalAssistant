namespace MyLocalAssistant.Server.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            time = DateTimeOffset.UtcNow,
        })).WithTags("Health");
        return app;
    }
}
