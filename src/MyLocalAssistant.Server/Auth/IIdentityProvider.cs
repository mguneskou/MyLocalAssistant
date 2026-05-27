namespace MyLocalAssistant.Server.Auth;

/// <summary>
/// External identity returned by an <see cref="IIdentityProvider"/> on a successful authentication.
/// The caller (UserService) maps groups to local departments and persists/upserts the User row.
/// </summary>
/// <param name="Username">Canonical username on the local side. Trimmed and case-normalized by the provider.</param>
/// <param name="DisplayName">Friendly display name. Falls back to username when the directory has none.</param>
/// <param name="Groups">Group identifiers (CN, sAMAccountName, or DN) the user is a direct/transitive member of.</param>
/// <param name="IsAdmin">True when the directory provider has determined the user is an administrator (e.g. member of the configured admin group).</param>
public sealed record ExternalIdentity(
    string Username,
    string DisplayName,
    IReadOnlyList<string> Groups,
    bool IsAdmin);

public enum IdentityAuthResult
{
    /// <summary>Provider authenticated the user — returned identity is non-null.</summary>
    Authenticated,
    /// <summary>Username/password was rejected by the provider.</summary>
    InvalidCredentials,
    /// <summary>Provider did not recognise the username (e.g. account not in directory). Caller may try the next provider.</summary>
    UserUnknown,
    /// <summary>Provider is disabled or unreachable. Caller should fall through to other providers.</summary>
    Unavailable,
}

public interface IIdentityProvider
{
    /// <summary>Stable name written to <see cref="Persistence.User.AuthSource"/>: "local", "ldap", ...</summary>
    string Name { get; }
    Task<(IdentityAuthResult Result, ExternalIdentity? Identity)> AuthenticateAsync(
        string username, string password, CancellationToken ct);
}
