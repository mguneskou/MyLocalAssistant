using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Admin.Services;

/// <summary>
/// Thin HTTP client for the MyLocalAssistant.Server API.
/// Single-instance, holds the current access/refresh tokens for the session.
/// On 401, automatically refreshes once and retries.
/// </summary>
public sealed class ServerClient : IDisposable
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessExpires;
    private bool _refreshing;

    public ServerClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyLocalAssistant.Admin/2.0");
    }

    public string BaseUrl { get; }
    public UserDto? CurrentUser { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("healthz", ct);
            return resp.IsSuccessStatusCode;
        }
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
        // After password change the server keeps the existing token valid; mark local user as no-longer-must-change.
        if (CurrentUser is not null)
        {
            CurrentUser = CurrentUser with { MustChangePassword = false };
        }
    }

    public void Logout()
    {
        _accessToken = null;
        _refreshToken = null;
        CurrentUser = null;
    }

    // ---------- Admin: users ----------

    public async Task<List<UserAdminDto>> ListUsersAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/users/", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<UserAdminDto>>(s_json, ct) ?? new();
    }

    public async Task<UserAdminDto> CreateUserAsync(CreateUserRequest req, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, "api/admin/users/", req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<UserAdminDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty create-user response.");
    }

    public async Task<UserAdminDto> UpdateUserAsync(Guid id, UpdateUserRequest req, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Patch, $"api/admin/users/{id}", req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<UserAdminDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty update-user response.");
    }

    public async Task ResetUserPasswordAsync(Guid id, string newPassword, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, $"api/admin/users/{id}/reset-password",
            new ResetPasswordRequest(newPassword), ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Delete, $"api/admin/users/{id}", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    // ---------- Admin: departments ----------

    public async Task<List<DepartmentDto>> ListDepartmentsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/departments/", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<DepartmentDto>>(s_json, ct) ?? new();
    }

    // ---------- Admin: models ----------

    public async Task<List<ModelDto>> ListModelsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/models/", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<ModelDto>>(s_json, ct) ?? new();
    }

    public async Task<ActiveModelStatusDto> GetModelStatusAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/models/status", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ActiveModelStatusDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty model-status response.");
    }

    public async Task StartDownloadAsync(string modelId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, $"api/admin/models/{modelId}/download", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task CancelDownloadAsync(string modelId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Delete, $"api/admin/models/{modelId}/download", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task ActivateModelAsync(string modelId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, $"api/admin/models/{modelId}/activate", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task DeleteModelAsync(string modelId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Delete, $"api/admin/models/{modelId}", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    // ---------- Admin: agents ----------

    public async Task<List<AgentDto>> ListAgentsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/agents/", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<AgentDto>>(s_json, ct) ?? new();
    }

    public async Task<AgentDto> UpdateAgentAsync(string id, AgentUpdateRequest req, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Patch, $"api/admin/agents/{id}", req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AgentDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty update-agent response.");
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

        // One refresh + retry attempt.
        resp.Dispose();
        if (!await TryRefreshAsync(ct))
        {
            return await SendOnceAsync(method, path, body, ct); // returns 401, caller decides
        }
        return await SendOnceAsync(method, path, body, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(_accessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: s_json);
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
        if (_refreshing || _refreshToken is null) return false;
        _refreshing = true;
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
        finally { _refreshing = false; }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        // Try to extract problem-details title which carries our ProblemCodes.* string.
        string detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                detail = d.GetString() ?? body;
        }
        catch { /* not json */ }
        throw new ServerApiException((int)resp.StatusCode, detail);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ServerApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
