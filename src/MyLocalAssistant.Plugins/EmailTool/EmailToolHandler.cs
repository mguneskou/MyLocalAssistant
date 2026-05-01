using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Identity.Client;
using MyLocalAssistant.Plugin.Shared;

namespace MyLocalAssistant.Plugins.EmailTool;

/// <summary>
/// Sends email via Microsoft Graph API using OAuth2 client_credentials (app-only) flow.
/// The Entra app registration must have admin-consented Mail.Send application permission.
/// Config JSON: {"tenantId":"...","clientId":"...","clientSecret":"...","fromAddress":"..."}
/// If fromAddress is omitted, context.Username (expected to be a UPN) is used as the sender.
/// </summary>
internal sealed class EmailToolHandler : IPluginTool
{
    private string? _tenantId;
    private string? _clientId;
    private string? _clientSecret;
    private string? _fromAddress;

    private IConfidentialClientApplication? _msal;
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

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
    }

    public async Task<PluginToolResult> InvokeAsync(
        string toolName, JsonElement arguments, PluginContext context, CancellationToken ct)
    {
        if (_msal is null)
            return PluginToolResult.Error(
                "Email tool is not configured. An administrator must set tenantId, clientId, " +
                "and clientSecret (Entra app registration with admin-consented Mail.Send permission).");

        var from = _fromAddress ?? context.Username;
        if (string.IsNullOrWhiteSpace(from))
            return PluginToolResult.Error("Cannot determine sender address — set fromAddress in config or ensure Username is a UPN.");

        // Parse arguments.
        var toList  = ParseStringArray(arguments, "to");
        var ccList  = ParseStringArray(arguments, "cc");
        var subject = arguments.TryGetProperty("subject", out var sub) ? sub.GetString() ?? "" : "";
        var body    = arguments.TryGetProperty("body",    out var bd)  ? bd.GetString()  ?? "" : "";
        var isHtml  = arguments.TryGetProperty("html",    out var html) && html.ValueKind == JsonValueKind.True;

        if (toList.Count == 0)   return PluginToolResult.Error("At least one 'to' address is required.");
        if (string.IsNullOrWhiteSpace(subject)) return PluginToolResult.Error("subject is required.");
        if (string.IsNullOrWhiteSpace(body))    return PluginToolResult.Error("body is required.");

        // Acquire token.
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
            return PluginToolResult.Error($"Failed to acquire access token: {ex.Message}");
        }

        // Build Graph sendMail payload.
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
                return PluginToolResult.Ok($"Email sent to {string.Join(", ", toList)}.");

            var errorBody = await response.Content.ReadAsStringAsync(ct);
            return PluginToolResult.Error(
                $"Graph API returned {(int)response.StatusCode}: {errorBody}");
        }
        catch (HttpRequestException ex)
        {
            return PluginToolResult.Error($"HTTP error sending email: {ex.Message}");
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

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Config
    {
        [JsonPropertyName("tenantId")]     public string? TenantId     { get; set; }
        [JsonPropertyName("clientId")]     public string? ClientId     { get; set; }
        [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
        [JsonPropertyName("fromAddress")]  public string? FromAddress  { get; set; }
    }
}
