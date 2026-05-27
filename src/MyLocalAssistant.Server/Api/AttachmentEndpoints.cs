using System.Security.Claims;
using System.Text;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Server.Rag;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class AttachmentEndpoints
{
    /// <summary>Hard cap on uploaded file size: 10 MiB.</summary>
    public const long MaxUploadBytes = 10L * 1024 * 1024;
    /// <summary>Hard cap on extracted text fed back to the model: 60 000 chars (~15k tokens).</summary>
    public const int MaxExtractedChars = 60_000;

    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/attachments/extract", HandleExtractAsync)
            .WithTags("Chat")
            .RequireAuthorization()
            .DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> HandleExtractAsync(
        HttpContext http,
        AuditWriter audit,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Attachments");
        var user = http.User;
        Guid.TryParse(user.FindFirstValue("sub"), out var userId);
        var username = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name);
        var ip = http.Connection.RemoteIpAddress?.ToString();

        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { detail = "multipart/form-data required." });

        IFormFile? file;
        try
        {
            var form = await http.Request.ReadFormAsync(ct);
            file = form.Files["file"] ?? form.Files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { detail = "Invalid form: " + ex.Message });
        }

        if (file is null || file.Length == 0)
            return Results.BadRequest(new { detail = "No file uploaded." });
        if (file.Length > MaxUploadBytes)
        {
            await audit.WriteAsync("chat.attach.denied", userId, username, success: false,
                detail: $"file={file.FileName}; size={file.Length}; reason=too_large",
                ipAddress: ip, ct: ct);
            return Results.Json(new { detail = $"File too large (max {MaxUploadBytes / (1024 * 1024)} MiB)." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var fileName = Path.GetFileName(file.FileName) ?? "attachment";
        if (!DocumentParsers.IsSupported(fileName))
        {
            await audit.WriteAsync("chat.attach.denied", userId, username, success: false,
                detail: $"file={fileName}; reason=unsupported_type",
                ipAddress: ip, ct: ct);
            return Results.BadRequest(new { detail = $"Unsupported file type: {Path.GetExtension(fileName)}" });
        }

        try
        {
            await using var ms = new MemoryStream();
            await using (var input = file.OpenReadStream())
            {
                await input.CopyToAsync(ms, ct);
            }
            ms.Position = 0;

            var pages = DocumentParsers.Parse(ms, fileName);

            var sb = new StringBuilder();
            var truncated = false;
            foreach (var p in pages)
            {
                var txt = p.Text;
                if (string.IsNullOrWhiteSpace(txt)) continue;
                var remaining = MaxExtractedChars - sb.Length;
                if (remaining <= 0) { truncated = true; break; }
                if (txt.Length > remaining)
                {
                    sb.Append(txt, 0, remaining);
                    truncated = true;
                    break;
                }
                sb.Append(txt);
                if (!txt.EndsWith('\n')) sb.Append('\n');
            }

            var text = sb.ToString();
            await audit.WriteAsync("chat.attach.extract", userId, username, success: true,
                detail: $"file={fileName}; size={file.Length}; pages={pages.Count}; chars={text.Length}; truncated={truncated}",
                ipAddress: ip, ct: ct);

            return Results.Ok(new AttachmentExtractResult(fileName, text.Length, pages.Count, truncated, text));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Attachment extract failed for {File}.", fileName);
            await audit.WriteAsync("chat.attach.extract", userId, username, success: false,
                detail: $"file={fileName}; error={ex.Message}", ipAddress: ip, ct: ct);
            return Results.BadRequest(new { detail = "Could not parse file: " + ex.Message });
        }
    }
}
