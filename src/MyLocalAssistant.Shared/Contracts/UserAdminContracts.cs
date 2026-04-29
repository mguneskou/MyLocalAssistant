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
    DateTimeOffset? LastLoginAt);

public sealed record CreateUserRequest(
    string Username,
    string DisplayName,
    string Password,
    IReadOnlyList<string>? Departments,
    bool IsAdmin);

public sealed record UpdateUserRequest(
    string? DisplayName,
    IReadOnlyList<string>? Departments,
    bool? IsAdmin,
    bool? IsDisabled);

public sealed record ResetPasswordRequest(string NewPassword);
