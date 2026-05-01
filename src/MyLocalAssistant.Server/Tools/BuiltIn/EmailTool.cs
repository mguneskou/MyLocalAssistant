using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Identity.Client;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Sends email via Microsoft Graph API using OAuth2 client_credentials (app-only) flow.
/// The Entra app registration must have admin-consented Mail.Send application permission.
/// Config JSON: {"tenantId":"...","clientId":"...","clientSecret":"...","fromAddress":"..."}
/// </summary>
internal sealed class EmailTool : ITool
{
    // ── ITool metadata ────────────────────────────────────────────────────────

    public string  Id          => "email.tool";
    public string  Name        => "Email Tool";
    public string  Description => "Send emails via Microsoft Graph API. Requires an Entra (Azure AD) app registration with admin-consented Mail.Send application permission.";
    public string  Category    => "Communication";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "email.send",
            Description: "Send an email to one or more recipients. Requires admin-configured Microsoft 365 credentials.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"to":{"oneOf":[{"type":"string"},{"type":"array","items":{"type":"string"}}],"description":"Recipient address or array of addresses"},"cc":{"oneOf":[{"type":"string"},{"type":"array","items":{"type":"string"}}],"description":"CC address(es)"},"subject":{"type":"string","description":"Email subject"},"body":{"type":"string","description":"Email body text"},"html":{"type":"boolean","description":"If true, body is treated as HTML (default false)"}},"required":["to","subject","body"]}"""),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Json, MinContextK: 4);

    // ── Config ────────────────────────────────────────────────────────────────

    private string? _tenantId;
    private string? _clientId;
    private string? _clientSecret;
    private string? _fromAddress;

    private IConfidentialClientApplication? _msal;
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        _tenantId     = cfg?.TenantId;
        _clientId     = cfg?.ClientId;
        _clientSecret = cfg?.ClientSecret;
        _fromAddress  = cfg?.FromAddress;

        if (!string.IsNullOrWhiteSpace(_tenantId) &&
            !string.IsNullOrWhiteSpace(_clientId)  &&
            !string.IsNullOrWhiteSpace(_clientSecret))
        {
            _msal = ConfidentialClientApplicationBuilder
                .Create(_clientId)
                .WithClientSecret(_clientSecret)
                .WithAuthority(AzureCloudInstance.AzurePublic, _tenantId)
                .Build();
        }
        else
        {
            _msal = null;
        }
    }

    // ── ITool.InvokeAsync ─────────────────────────────────────────────────────

    public async Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        if (_msal is null)
            return ToolResult.Error(
                "Email tool is not configured. An administrator must set tenantId, clientId, " +
                "and clientSecret (Entra app registration with admin-consented Mail.Send permission).");

        var from = _fromAddress ?? ctx.Username;
        if (string.IsNullOrWhiteSpace(from))
            return ToolResult.Error("Cannot determine sender address — set fromAddress in config or ensure Username is a UPN.");

        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args = doc.RootElement;
        var ct   = ctx.CancellationToken;

        var toList  = ParseStringArray(args, "to");
        var ccList  = ParseStringArray(args, "cc");
        var subject = args.TryGetProperty("subject", out var sub) ? sub.GetString() ?? "" : "";
        var body    = args.TryGetProperty("body",    out var bd)  ? bd.GetString()  ?? "" : "";
        var isHtml  = args.TryGetProperty("html",    out var html) && html.ValueKind == JsonValueKind.True;

        if (toList.Count == 0)                  return ToolResult.Error("At least one 'to' address is required.");
        if (string.IsNullOrWhiteSpace(subject)) return ToolResult.Error("subject is required.");
        if (string.IsNullOrWhiteSpace(body))    return ToolResult.Error("body is required.");

        string token;
        try
        {
            var result = await _msal
                .AcquireTokenForClient(["https://graph.microsoft.com/.default"])
                .ExecuteAsync(ct);
            token = result.AccessToken;
        }
        catch (MsalException ex)
        {
            return ToolResult.Error($"Failed to acquire access token: {ex.Message}");
        }

        var payload = new
        {
            message = new
            {
                subject,
                body = new { contentType = isHtml ? "HTML" : "Text", content = body },
                toRecipients = toList.Select(a => new { emailAddress = new { address = a } }),
                ccRecipients = ccList.Select(a => new { emailAddress = new { address = a } }),
            },
            saveToSentItems = true,
        };
        var json    = JsonSerializer.Serialize(payload, s_json);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(from)}/sendMail")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var response = await s_http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return ToolResult.Ok($"Email sent to {string.Join(", ", toList)}.");

            var errorBody = await response.Content.ReadAsStringAsync(ct);
            return ToolResult.Error($"Graph API returned {(int)response.StatusCode}: {errorBody}");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"HTTP error sending email: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<string> ParseStringArray(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var arr)) return [];
        if (arr.ValueKind == JsonValueKind.String) return [arr.GetString()!];
        if (arr.ValueKind != JsonValueKind.Array)  return [];
        return arr.EnumerateArray()
                  .Where(x => x.ValueKind == JsonValueKind.String)
                  .Select(x => x.GetString()!)
                  .Where(s => !string.IsNullOrWhiteSpace(s))
                  .ToList();
    }

    private sealed class Config
    {
        [JsonPropertyName("tenantId")]     public string? TenantId     { get; set; }
        [JsonPropertyName("clientId")]     public string? ClientId     { get; set; }
        [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
        [JsonPropertyName("fromAddress")]  public string? FromAddress  { get; set; }
    }
}
