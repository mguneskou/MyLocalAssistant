namespace MyLocalAssistant.Shared.Contracts;

public sealed record UserAdminDto(
    Guid Id,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Departments,
    bool IsAdmin,
    bool IsDisabled,
    bool MustChangePassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    string? WorkRoot);

public sealed record CreateUserRequest(
    string Username,
    string DisplayName,
    string Password,
    IReadOnlyList<string>? Departments,
    bool IsAdmin,
    string? WorkRoot);

public sealed record UpdateUserRequest(
    string? DisplayName,
    IReadOnlyList<string>? Departments,
    bool? IsAdmin,
    bool? IsDisabled,
    string? WorkRoot);

public sealed record ResetPasswordRequest(string NewPassword);
