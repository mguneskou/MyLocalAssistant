namespace MyLocalAssistant.Shared.Contracts;

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    UserDto User);

public sealed record RefreshRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UserDto(
    Guid Id,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Departments,
    IReadOnlyList<string> Roles,
    bool MustChangePassword,
    bool IsAdmin);
