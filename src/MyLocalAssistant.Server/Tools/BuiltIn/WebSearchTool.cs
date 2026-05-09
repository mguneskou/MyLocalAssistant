using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Tools.BuiltIn;

/// <summary>
/// Searches the web via Bing, Brave Search, or DuckDuckGo and can fetch/extract text from a URL.
/// Config JSON: {"provider":"bing","apiKey":"...","maxResults":5}
/// </summary>
internal sealed class WebSearchTool : ITool
{
    // ── ITool metadata ────────────────────────────────────────────────────────

    public string  Id          => "web.search";
    public string  Name        => "Web Search";
    public string  Description => "Searches the web via Bing, Brave Search, or DuckDuckGo and can visit/extract text from a URL.";
    public string  Category    => "Research";
    public string  Source      => ToolSources.BuiltIn;
    public string? Version     => null;
    public string? Publisher   => "MyLocalAssistant";
    public string? KeyId       => null;

    public IReadOnlyList<ToolFunctionDto> Tools { get; } = new[]
    {
        new ToolFunctionDto(
            Name: "web.search",
            Description: "Search the web for up-to-date information. Returns a numbered list of results with titles, snippets, and URLs.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"query":{"type":"string","description":"The search query"},"max_results":{"type":"integer","description":"Number of results to return (1–10, default 5)","minimum":1,"maximum":10}},"required":["query"]}"""),
        new ToolFunctionDto(
            Name: "web.visit",
            Description: "Fetch a URL and extract the readable text content. Useful for reading articles, documentation, or search result pages.",
            ArgumentsSchemaJson: """{"type":"object","properties":{"url":{"type":"string","description":"The https URL to visit"},"max_chars":{"type":"integer","description":"Maximum characters to return (default 4000)"}},"required":["url"]}"""),
    };

    public ToolRequirementsDto Requirements { get; } = new(ToolCallProtocols.Json, MinContextK: 4);

    // ── HTTP client ───────────────────────────────────────────────────────────

    private static readonly HttpClient s_http;

    static WebSearchTool()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        s_http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        s_http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MyLocalAssistant/2.3)");
    }

    // ── Config ────────────────────────────────────────────────────────────────

    private string  _provider   = "duckduckgo";
    private string? _apiKey;
    private int     _maxResults = 5;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (cfg is null) return;
        if (!string.IsNullOrWhiteSpace(cfg.Provider))   _provider   = cfg.Provider.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))     _apiKey     = cfg.ApiKey;
        if (cfg.MaxResults is > 0 and <= 20)            _maxResults = cfg.MaxResults.Value;
    }

    // ── ITool.InvokeAsync ─────────────────────────────────────────────────────

    public async Task<ToolResult> InvokeAsync(ToolInvocation call, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var args = doc.RootElement;
        var ct   = ctx.CancellationToken;

        var result = call.ToolName switch
        {
            "web.search" => await SearchAsync(args, ct),
            "web.visit"  => await VisitAsync(args, ct),
            _            => ToolResult.Error($"Unknown tool '{call.ToolName}'"),
        };
        return result;
    }

    // ── web.search ────────────────────────────────────────────────────────────

    private async Task<ToolResult> SearchAsync(JsonElement args, CancellationToken ct)
    {
        var query      = args.TryGetProperty("query",       out var q)  ? q.GetString() ?? "" : "";
        var maxResults = args.TryGetProperty("max_results", out var mr) && mr.TryGetInt32(out var n) ? n : _maxResults;
        maxResults = Math.Clamp(maxResults, 1, 20);

        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Error("query is required");

        try
        {
            return _provider switch
            {
                "bing"  => await BingSearchAsync(query, maxResults, ct),
                "brave" => await BraveSearchAsync(query, maxResults, ct),
                _       => await DdgSearchAsync(query, maxResults, ct),
            };
        }
        catch (TaskCanceledException)   { return ToolResult.Error("Search request timed out."); }
        catch (Exception ex)            { return ToolResult.Error($"Search failed: {ex.Message}"); }
    }

    private async Task<ToolResult> BingSearchAsync(string query, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return await DdgSearchAsync(query, max, ct);

        var url = $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count={max}&mkt=en-US";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
        using var resp = await s_http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var bdoc = JsonDocument.Parse(json);

        var sb = new System.Text.StringBuilder();
        int i  = 0;
        if (bdoc.RootElement.TryGetProperty("webPages", out var pages) &&
            pages.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (i++ >= max) break;
                var name    = item.TryGetProperty("name",    out var nm) ? nm.GetString()  : "";
                var snippet = item.TryGetProperty("snippet", out var sn) ? sn.GetString()  : "";
                var itemUrl = item.TryGetProperty("url",     out var u)  ? u.GetString()   : "";
                sb.AppendLine($"[{i}] {name}");
                sb.AppendLine($"    {snippet}");
                sb.AppendLine($"    URL: {itemUrl}");
            }
        }
        return sb.Length == 0
            ? ToolResult.Error("No results found.")
            : ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private async Task<ToolResult> BraveSearchAsync(string query, int max, CancellationToken ct)
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
        var bdoc = JsonDocument.Parse(json);

        var sb = new System.Text.StringBuilder();
        int i  = 0;
        if (bdoc.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                if (i++ >= max) break;
                var title       = item.TryGetProperty("title",       out var t) ? t.GetString() : "";
                var description = item.TryGetProperty("description", out var d) ? d.GetString() : "";
                var itemUrl     = item.TryGetProperty("url",         out var u) ? u.GetString() : "";
                sb.AppendLine($"[{i}] {title}");
                sb.AppendLine($"    {description}");
                sb.AppendLine($"    URL: {itemUrl}");
            }
        }
        return sb.Length == 0
            ? ToolResult.Error("No results found.")
            : ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static readonly Regex s_ddgResult = new(
        @"<a class=""result__a"" href=""(.+?)"">(.+?)</a>.*?<a class=""result__snippet"">(.+?)</a>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private async Task<ToolResult> DdgSearchAsync(string query, int max, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/");
        req.Content = new FormUrlEncodedContent([new("q", query)]);
        using var resp = await s_http.SendAsync(req, ct);
        var html = await resp.Content.ReadAsStringAsync(ct);

        // Try HtmlAgilityPack first (robust against DDG HTML changes).
        var results = ParseDdgHtml(html, max);
        if (results.Length > 0)
            return ToolResult.Ok(results);

        // Fallback to legacy regex if HAP found nothing.
        var matches = s_ddgResult.Matches(html);
        if (matches.Count == 0)
            return ToolResult.Error("No results found (DuckDuckGo).");

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
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static string ParseDdgHtml(string html, int max)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // DDG result links are inside <a> elements with class "result__a" or inside divs
        // with class "result__body". Use attribute selectors for resilience.
        var resultNodes = doc.DocumentNode.SelectNodes(
            "//div[contains(@class,'result') and not(contains(@class,'result--ad'))]");
        if (resultNodes is null) return "";

        var sb = new System.Text.StringBuilder();
        int i = 0;
        foreach (var node in resultNodes)
        {
            if (i >= max) break;
            var titleNode   = node.SelectSingleNode(".//a[@class='result__a']") ??
                              node.SelectSingleNode(".//h2//a") ??
                              node.SelectSingleNode(".//a[contains(@class,'result')]");
            var snippetNode = node.SelectSingleNode(".//*[contains(@class,'result__snippet')]") ??
                              node.SelectSingleNode(".//*[contains(@class,'snippet')]");
            if (titleNode is null) continue;

            var href    = WebUtility.HtmlDecode(titleNode.GetAttributeValue("href", ""));
            var title   = WebUtility.HtmlDecode(titleNode.InnerText).Trim();
            var snippet = snippetNode is null ? "" : WebUtility.HtmlDecode(snippetNode.InnerText).Trim();

            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title)) continue;
            // Skip DDG-internal links.
            if (href.StartsWith("/", StringComparison.Ordinal) && !href.StartsWith("//", StringComparison.Ordinal)) continue;

            i++;
            sb.AppendLine($"[{i}] {title}");
            if (!string.IsNullOrWhiteSpace(snippet)) sb.AppendLine($"    {snippet}");
            sb.AppendLine($"    URL: {href}");
        }
        return sb.ToString().TrimEnd();
    }

    // ── web.visit ─────────────────────────────────────────────────────────────

    private async Task<ToolResult> VisitAsync(JsonElement args, CancellationToken ct)
    {
        var url      = args.TryGetProperty("url",       out var u)  ? u.GetString() ?? "" : "";
        var maxChars = args.TryGetProperty("max_chars", out var mc) && mc.TryGetInt32(out var n) ? n : 4000;
        maxChars = Math.Clamp(maxChars, 200, 16_000);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return ToolResult.Error("url must be a valid http/https URL");

        try
        {
            var html = await s_http.GetStringAsync(uri, ct);
            var text = ExtractText(html);
            if (text.Length > maxChars) text = text[..maxChars] + "\n… (truncated)";
            return ToolResult.Ok(text);
        }
        catch (TaskCanceledException) { return ToolResult.Error("Request timed out."); }
        catch (Exception ex)          { return ToolResult.Error($"Failed to fetch URL: {ex.Message}"); }
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

    private sealed class Config
    {
        [JsonPropertyName("provider")]   public string? Provider   { get; set; }
        [JsonPropertyName("apiKey")]     public string? ApiKey     { get; set; }
        [JsonPropertyName("maxResults")] public int?    MaxResults { get; set; }
    }
}
