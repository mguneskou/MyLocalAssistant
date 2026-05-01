using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MyLocalAssistant.Plugin.Shared;

namespace MyLocalAssistant.Plugins.WebSearch;

/// <summary>
/// Supports web.search (Bing → Brave → DuckDuckGo scrape fallback) and web.visit
/// (fetch a URL and extract readable text).
/// Config JSON: {"provider":"bing","apiKey":"...","maxResults":5}
/// </summary>
internal sealed class WebSearchHandler : IPluginTool
{
    private static readonly HttpClient s_http;

    static WebSearchHandler()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        s_http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        s_http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; MyLocalAssistant/2.3)");
    }

    private string  _provider   = "duckduckgo";
    private string? _apiKey;
    private int     _maxResults = 5;

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (cfg is null) return;
        if (!string.IsNullOrWhiteSpace(cfg.Provider))   _provider   = cfg.Provider.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))     _apiKey     = cfg.ApiKey;
        if (cfg.MaxResults is > 0 and <= 20)            _maxResults = cfg.MaxResults.Value;
    }

    public async Task<PluginToolResult> InvokeAsync(
        string toolName, JsonElement arguments, PluginContext context, CancellationToken ct)
    {
        return toolName switch
        {
            "web.search" => await SearchAsync(arguments, ct),
            "web.visit"  => await VisitAsync(arguments, ct),
            _            => PluginToolResult.Error($"Unknown tool '{toolName}'"),
        };
    }

    // ── web.search ────────────────────────────────────────────────────────────

    private async Task<PluginToolResult> SearchAsync(JsonElement args, CancellationToken ct)
    {
        var query      = args.TryGetProperty("query",       out var q)  ? q.GetString() ?? "" : "";
        var maxResults = args.TryGetProperty("max_results", out var mr) && mr.TryGetInt32(out var n) ? n : _maxResults;
        maxResults = Math.Clamp(maxResults, 1, 20);

        if (string.IsNullOrWhiteSpace(query))
            return PluginToolResult.Error("query is required");

        try
        {
            return _provider switch
            {
                "bing"  => await BingSearchAsync(query, maxResults, ct),
                "brave" => await BraveSearchAsync(query, maxResults, ct),
                _       => await DdgSearchAsync(query, maxResults, ct),
            };
        }
        catch (TaskCanceledException)   { return PluginToolResult.Error("Search request timed out."); }
        catch (Exception ex)            { return PluginToolResult.Error($"Search failed: {ex.Message}"); }
    }

    private async Task<PluginToolResult> BingSearchAsync(string query, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return await DdgSearchAsync(query, max, ct); // fallback

        var url = $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count={max}&mkt=en-US";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
        using var resp = await s_http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc  = JsonDocument.Parse(json);

        var sb = new System.Text.StringBuilder();
        int i  = 0;
        if (doc.RootElement.TryGetProperty("webPages", out var pages) &&
            pages.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (i++ >= max) break;
                var name    = item.TryGetProperty("name",    out var nm)  ? nm.GetString()  : "";
                var snippet = item.TryGetProperty("snippet", out var sn)  ? sn.GetString()  : "";
                var itemUrl = item.TryGetProperty("url",     out var u)   ? u.GetString()   : "";
                sb.AppendLine($"[{i}] {name}");
                sb.AppendLine($"    {snippet}");
                sb.AppendLine($"    URL: {itemUrl}");
            }
        }
        return sb.Length == 0
            ? PluginToolResult.Error("No results found.")
            : PluginToolResult.Ok(sb.ToString().TrimEnd());
    }

    private async Task<PluginToolResult> BraveSearchAsync(string query, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return await DdgSearchAsync(query, max, ct);

        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={max}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("X-Subscription-Token", _apiKey);
        using var resp = await s_http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc  = JsonDocument.Parse(json);

        var sb = new System.Text.StringBuilder();
        int i  = 0;
        if (doc.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                if (i++ >= max) break;
                var title       = item.TryGetProperty("title",       out var t)  ? t.GetString()  : "";
                var description = item.TryGetProperty("description", out var d)  ? d.GetString()  : "";
                var itemUrl     = item.TryGetProperty("url",         out var u)  ? u.GetString()  : "";
                sb.AppendLine($"[{i}] {title}");
                sb.AppendLine($"    {description}");
                sb.AppendLine($"    URL: {itemUrl}");
            }
        }
        return sb.Length == 0
            ? PluginToolResult.Error("No results found.")
            : PluginToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static readonly Regex s_ddgResult = new(
        @"<a class=""result__a"" href=""(.+?)"">(.+?)</a>.*?<a class=""result__snippet"">(.+?)</a>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private async Task<PluginToolResult> DdgSearchAsync(string query, int max, CancellationToken ct)
    {
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/");
        req.Content = new FormUrlEncodedContent([new("q", query)]);
        using var resp = await s_http.SendAsync(req, ct);
        var html = await resp.Content.ReadAsStringAsync(ct);

        // Minimal HTML parse — DDG's HTML endpoint is stable and tag-simple.
        var matches = s_ddgResult.Matches(html);
        if (matches.Count == 0)
            return PluginToolResult.Error("No results found (DuckDuckGo).");

        var sb = new System.Text.StringBuilder();
        int i  = 0;
        foreach (Match m in matches)
        {
            if (i++ >= max) break;
            var href    = WebUtility.HtmlDecode(m.Groups[1].Value);
            var title   = WebUtility.HtmlDecode(StripTags(m.Groups[2].Value));
            var snippet = WebUtility.HtmlDecode(StripTags(m.Groups[3].Value));
            sb.AppendLine($"[{i}] {title}");
            sb.AppendLine($"    {snippet}");
            sb.AppendLine($"    URL: {href}");
        }
        return PluginToolResult.Ok(sb.ToString().TrimEnd());
    }

    // ── web.visit ─────────────────────────────────────────────────────────────

    private async Task<PluginToolResult> VisitAsync(JsonElement args, CancellationToken ct)
    {
        var url      = args.TryGetProperty("url",       out var u)  ? u.GetString() ?? "" : "";
        var maxChars = args.TryGetProperty("max_chars", out var mc) && mc.TryGetInt32(out var n) ? n : 4000;
        maxChars = Math.Clamp(maxChars, 200, 16_000);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return PluginToolResult.Error("url must be a valid http/https URL");

        try
        {
            var html = await s_http.GetStringAsync(uri, ct);
            var text = ExtractText(html);
            if (text.Length > maxChars) text = text[..maxChars] + "\n… (truncated)";
            return PluginToolResult.Ok(text);
        }
        catch (TaskCanceledException) { return PluginToolResult.Error("Request timed out."); }
        catch (Exception ex)          { return PluginToolResult.Error($"Failed to fetch URL: {ex.Message}"); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly Regex s_tags    = new(@"<[^>]+>",  RegexOptions.Compiled);
    private static readonly Regex s_scripts = new(@"<(script|style)[^>]*>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_ws      = new(@"\s{2,}",   RegexOptions.Compiled);

    private static string StripTags(string html) => s_tags.Replace(html, "");

    private static string ExtractText(string html)
    {
        var stripped = s_scripts.Replace(html, " ");
        stripped = s_tags.Replace(stripped, " ");
        stripped = WebUtility.HtmlDecode(stripped);
        stripped = s_ws.Replace(stripped, " ");
        return stripped.Trim();
    }

    // ── Config ────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private sealed class Config
    {
        [JsonPropertyName("provider")]   public string?  Provider   { get; set; }
        [JsonPropertyName("apiKey")]     public string?  ApiKey     { get; set; }
        [JsonPropertyName("maxResults")] public int?     MaxResults { get; set; }
    }
}

// Use the shared framing options from Shared lib
file static class JsonRpcFraming
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
