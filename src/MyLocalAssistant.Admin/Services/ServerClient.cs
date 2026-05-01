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

    public async Task ActivateEmbeddingAsync(string modelId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, $"api/admin/models/embedding/{modelId}/activate", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<ActiveEmbeddingStatusDto> GetEmbeddingStatusAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/models/embedding/status", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ActiveEmbeddingStatusDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty embedding-status response.");
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

    // ---------- Admin: tools ----------

    public async Task<List<ToolDto>> ListToolsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/tools/", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<ToolDto>>(s_json, ct) ?? new();
    }

    public async Task<ToolDto> UpdateToolAsync(string id, ToolUpdateRequest req, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Patch, $"api/admin/tools/{id}", req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ToolDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty update-tool response.");
    }

    /// <summary>Hot-reload all plug-in tools (rescans <c>./plugins/</c>). Owner-only.</summary>
    public async Task<int> ReloadPluginsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, "api/admin/tools/reload", new { }, ct);
        await EnsureSuccessAsync(resp, ct);
        var doc = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(s_json, ct);
        return doc.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 0;
    }

    // ---------- Admin: RAG ----------

    public async Task<List<RagCollectionDto>> ListCollectionsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/rag/collections", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<RagCollectionDto>>(s_json, ct) ?? new();
    }

    public async Task<RagCollectionDto> CreateCollectionAsync(string name, string? description, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, "api/admin/rag/collections",
            new CreateCollectionRequest(name, description), ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RagCollectionDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty create-collection response.");
    }

    public async Task DeleteCollectionAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Delete, $"api/admin/rag/collections/{id}", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<List<RagDocumentDto>> ListDocumentsAsync(Guid collectionId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, $"api/admin/rag/collections/{collectionId}/documents", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<RagDocumentDto>>(s_json, ct) ?? new();
    }

    public async Task<RagDocumentDto> UploadDocumentAsync(Guid collectionId, string filePath, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        using var content = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".html" or ".htm" => "text/html",
            ".md" => "text/markdown",
            _ => "text/plain",
        };
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/admin/rag/collections/{collectionId}/documents")
        {
            Content = content,
        };
        if (!string.IsNullOrEmpty(_accessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RagDocumentDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty upload response.");
    }

    public async Task DeleteDocumentAsync(Guid collectionId, Guid docId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Delete, $"api/admin/rag/collections/{collectionId}/documents/{docId}", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<RagCollectionDto> UpdateCollectionAsync(Guid id, UpdateCollectionRequest req, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Patch, $"api/admin/rag/collections/{id}", req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<RagCollectionDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty update-collection response.");
    }

    public async Task<List<CollectionGrantDto>> ListGrantsAsync(Guid collectionId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, $"api/admin/rag/collections/{collectionId}/grants", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<CollectionGrantDto>>(s_json, ct) ?? new();
    }

    public async Task<CollectionGrantDto> AddGrantAsync(Guid collectionId, string principalKind, Guid principalId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, $"api/admin/rag/collections/{collectionId}/grants",
            new AddCollectionGrantRequest(principalKind, principalId), ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<CollectionGrantDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty add-grant response.");
    }

    public async Task RemoveGrantAsync(Guid collectionId, long grantId, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Delete, $"api/admin/rag/collections/{collectionId}/grants/{grantId}", null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<List<RoleDto>> ListRolesAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/roles/", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<RoleDto>>(s_json, ct) ?? new();
    }

    public async Task<AuditPageDto> ListAuditAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? action = null,
        string? user = null,
        bool? success = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var qs = BuildAuditQuery(from, to, action, user, success);
        if (qs.Length > 0) qs += "&"; else qs = "?";
        qs += $"skip={skip}&take={take}";
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/audit/" + qs, null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AuditPageDto>(s_json, ct)
            ?? new AuditPageDto(new(), 0, skip, take);
    }

    public async Task<List<string>> ListAuditActionsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/audit/actions", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<List<string>>(s_json, ct) ?? new();
    }

    public async Task DownloadAuditCsvAsync(
        string targetPath,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? action = null,
        string? user = null,
        bool? success = null,
        CancellationToken ct = default)
    {
        var qs = BuildAuditQuery(from, to, action, user, success);
        await EnsureFreshTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/admin/audit/export.csv" + qs);
        if (!string.IsNullOrEmpty(_accessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(resp, ct);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(targetPath);
        await src.CopyToAsync(dst, ct);
    }

    private static string BuildAuditQuery(
        DateTimeOffset? from, DateTimeOffset? to, string? action, string? user, bool? success)
    {
        var parts = new List<string>();
        if (from is { } f) parts.Add("from=" + Uri.EscapeDataString(f.ToString("o")));
        if (to is { } t) parts.Add("to=" + Uri.EscapeDataString(t.ToString("o")));
        if (!string.IsNullOrWhiteSpace(action)) parts.Add("action=" + Uri.EscapeDataString(action));
        if (!string.IsNullOrWhiteSpace(user)) parts.Add("user=" + Uri.EscapeDataString(user));
        if (success is bool s) parts.Add("success=" + (s ? "true" : "false"));
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    public async Task<ServerSettingsDto> GetServerSettingsAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/settings/", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ServerSettingsDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty settings response.");
    }

    public async Task<ServerSettingsDto> UpdateServerSettingsAsync(UpdateServerSettingsRequest req, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Patch, "api/admin/settings/", req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ServerSettingsDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty settings response.");
    }

    // ---------- Owner: global system prompt ----------

    public async Task<string> GetGlobalSystemPromptAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/settings/global-prompt", null, ct);
        await EnsureSuccessAsync(resp, ct);
        var dto = await resp.Content.ReadFromJsonAsync<GlobalSystemPromptDto>(s_json, ct);
        return dto?.SystemPrompt ?? "";
    }

    public async Task<string> SetGlobalSystemPromptAsync(string prompt, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Put, "api/admin/settings/global-prompt", new UpdateGlobalSystemPromptRequest(prompt), ct);
        await EnsureSuccessAsync(resp, ct);
        var dto = await resp.Content.ReadFromJsonAsync<GlobalSystemPromptDto>(s_json, ct);
        return dto?.SystemPrompt ?? "";
    }

    // ---------- Owner: cloud LLM keys (v2.3) ----------

    public async Task<CloudKeysStatusDto> GetCloudKeysAsync(CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Get, "api/admin/settings/cloud-keys", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<CloudKeysStatusDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty cloud-keys response.");
    }

    public async Task<CloudKeysStatusDto> SetCloudKeysAsync(UpdateCloudKeysRequest req, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Put, "api/admin/settings/cloud-keys", req, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<CloudKeysStatusDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty cloud-keys response.");
    }

    public async Task<CloudKeyTestResultDto> TestCloudKeyAsync(string provider, CancellationToken ct = default)
    {
        var resp = await SendAuthorizedAsync(HttpMethod.Post, $"api/admin/settings/cloud-keys/test/{provider}", null, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<CloudKeyTestResultDto>(s_json, ct)
            ?? throw new InvalidOperationException("Empty test response.");
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
