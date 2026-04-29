using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Client.Services;

/// <summary>
/// Minimal HTTP client used by the end-user Client app. Talks to the same server as Admin
/// but only the surfaces a non-admin user needs: auth, list agents, stream chat.
/// </summary>
public sealed class ChatApiClient : IDisposable
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessExpires;

    public ChatApiClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyLocalAssistant.Client/2.0");
    }

    public string BaseUrl { get; }
    public UserDto? CurrentUser { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { using var r = await _http.GetAsync("healthz", ct); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<LoginResponse> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("api/auth/login",
            new LoginRequest(username, password), s_json, ct);
        await EnsureSuccessAsync(resp, ct);
        var login = await resp.Content.ReadFromJsonAsync<LoginResponse>(s_json, ct)
            ?? throw new InvalidOperationException("Empty login response.");
        SetTokens(login);
        return login;
    }

    public async Task ChangePasswordAsync(string current, string next, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, "api/auth/change-password",
            new ChangePasswordRequest(current, next), ct);
        await EnsureSuccessAsync(resp, ct);
        if (CurrentUser is not null) CurrentUser = CurrentUser with { MustChangePassword = false };
    }

    public void Logout() { _accessToken = null; _refreshToken = null; CurrentUser = null; }

    public async Task<List<AgentDto>> ListAgentsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/agents", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<AgentDto>>(s_json, ct) ?? new();
    }

    public async Task<List<ConversationSummaryDto>> ListConversationsAsync(string? agentId = null, CancellationToken ct = default)
    {
        var path = string.IsNullOrEmpty(agentId)
            ? "api/chat/conversations/"
            : $"api/chat/conversations/?agentId={Uri.EscapeDataString(agentId)}";
        var resp = await SendAuthorizedAsync(HttpMethod.Get, path, null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<ConversationSummaryDto>>(s_json, ct) ?? new();
    }

    public async Task<ConversationDetailDto?> GetConversationAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, $"api/chat/conversations/{id}", null, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ConversationDetailDto>(s_json, ct);
    }

    public async Task DeleteConversationAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Delete, $"api/chat/conversations/{id}", null, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>
    /// Streams chat tokens. Yields each <see cref="TokenStreamFrame"/> as it arrives over SSE.
    /// Cancellation closes the underlying HTTP connection.
    /// </summary>
    public async IAsyncEnumerable<TokenStreamFrame> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/chat/stream");
        if (!string.IsNullOrEmpty(_accessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = JsonContent.Create(request, options: s_json);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new ServerApiException((int)resp.StatusCode, ExtractDetail(body));
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line.AsSpan(5).TrimStart().ToString();
            if (payload.Length == 0) continue;
            TokenStreamFrame? frame = null;
            try { frame = JsonSerializer.Deserialize<TokenStreamFrame>(payload, s_json); }
            catch { /* malformed line; skip */ }
            if (frame is not null) yield return frame;
        }
    }

    private void SetTokens(LoginResponse login)
    {
        _accessToken = login.AccessToken;
        _refreshToken = login.RefreshToken;
        _accessExpires = login.ExpiresAt;
        CurrentUser = login.User;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);
        var resp = await SendOnceAsync(method, path, body, ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.Unauthorized) return resp;
        resp.Dispose();
        if (await TryRefreshAsync(ct))
            return await SendOnceAsync(method, path, body, ct);
        return await SendOnceAsync(method, path, body, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(_accessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (body is not null) req.Content = JsonContent.Create(body, options: s_json);
        return await _http.SendAsync(req, ct);
    }

    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (_accessToken is null || _refreshToken is null) return;
        if (_accessExpires - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(30)) return;
        await TryRefreshAsync(ct);
    }

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        if (_refreshToken is null) return false;
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/auth/refresh",
                new RefreshRequest(_refreshToken), s_json, ct);
            if (!resp.IsSuccessStatusCode) { Logout(); return false; }
            var login = await resp.Content.ReadFromJsonAsync<LoginResponse>(s_json, ct);
            if (login is null) { Logout(); return false; }
            SetTokens(login);
            return true;
        }
        catch { Logout(); return false; }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new ServerApiException((int)resp.StatusCode, ExtractDetail(body));
    }

    private static string ExtractDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString() ?? body;
        }
        catch { }
        return body;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ServerApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
