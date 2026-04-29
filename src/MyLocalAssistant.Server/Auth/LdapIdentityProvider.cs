using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using MyLocalAssistant.Server.Configuration;

namespace MyLocalAssistant.Server.Auth;

/// <summary>
/// LDAP/AD identity provider. Bind-and-search pattern:
///   1. Bind with the configured service account (or anonymously) and look up the user DN.
///   2. Re-bind with the user DN + supplied password to verify credentials.
///   3. Read group memberships and map to a local <see cref="ExternalIdentity"/>.
///
/// Network/connection failures return <see cref="IdentityAuthResult.Unavailable"/> so the
/// caller can fall back to local auth (useful when the DC is briefly unreachable).
/// </summary>
public sealed class LdapIdentityProvider(ServerSettings settings, ILogger<LdapIdentityProvider> log) : IIdentityProvider
{
    public string Name => "ldap";

    public Task<(IdentityAuthResult Result, ExternalIdentity? Identity)> AuthenticateAsync(
        string username, string password, CancellationToken ct)
    {
        // System.DirectoryServices.Protocols is sync only. Run on the thread pool to keep the request thread free.
        return Task.Run(() => Authenticate(username, password), ct);
    }

    private (IdentityAuthResult, ExternalIdentity?) Authenticate(string username, string password)
    {
        var ldap = settings.Ldap;
        if (!ldap.Enabled) return (IdentityAuthResult.Unavailable, null);
        if (string.IsNullOrWhiteSpace(ldap.Server) || string.IsNullOrWhiteSpace(ldap.BaseDn))
        {
            log.LogWarning("LDAP enabled but Server/BaseDn not configured. Skipping.");
            return (IdentityAuthResult.Unavailable, null);
        }
        if (string.IsNullOrEmpty(password))
        {
            // RFC 4513 §5.1.2: empty password = anonymous bind, which would return success and bypass auth.
            return (IdentityAuthResult.InvalidCredentials, null);
        }

        var id = new LdapDirectoryIdentifier(ldap.Server, ldap.Port);
        try
        {
            using var conn = new LdapConnection(id)
            {
                AuthType = AuthType.Basic,
            };
            conn.SessionOptions.ProtocolVersion = 3;
            if (ldap.UseSsl) conn.SessionOptions.SecureSocketLayer = true;
            conn.Timeout = TimeSpan.FromSeconds(15);

            // Step 1: bind with service account (or anonymously) to look the user up.
            var serviceCreds = string.IsNullOrEmpty(ldap.BindDn)
                ? null
                : new NetworkCredential(ldap.BindDn, ldap.BindPassword);
            try
            {
                if (serviceCreds is null) conn.Bind();
                else conn.Bind(serviceCreds);
            }
            catch (LdapException ex)
            {
                log.LogWarning(ex, "LDAP service bind failed (BindDn='{Dn}', server='{Srv}').", ldap.BindDn, ldap.Server);
                return (IdentityAuthResult.Unavailable, null);
            }

            var filter = string.Format(ldap.UserFilter, EscapeLdapFilter(username));
            var search = new SearchRequest(
                ldap.BaseDn, filter, SearchScope.Subtree,
                ldap.UsernameAttribute, ldap.DisplayNameAttribute, "memberOf", "distinguishedName");
            SearchResponse resp;
            try
            {
                resp = (SearchResponse)conn.SendRequest(search);
            }
            catch (DirectoryOperationException ex)
            {
                log.LogWarning(ex, "LDAP search failed for '{User}'.", username);
                return (IdentityAuthResult.UserUnknown, null);
            }
            if (resp.Entries.Count == 0) return (IdentityAuthResult.UserUnknown, null);
            var entry = resp.Entries[0];

            // Step 2: re-bind as the user to verify their password.
            try
            {
                conn.Bind(new NetworkCredential(entry.DistinguishedName, password));
            }
            catch (LdapException)
            {
                // Wrong password (most common) or account disabled in AD.
                return (IdentityAuthResult.InvalidCredentials, null);
            }

            var samName = GetAttr(entry, ldap.UsernameAttribute) ?? username;
            var display = GetAttr(entry, ldap.DisplayNameAttribute) ?? samName;
            var groups = GetAllAttr(entry, "memberOf");

            var isAdmin = !string.IsNullOrWhiteSpace(ldap.AdminGroup)
                && MatchesGroup(groups, ldap.AdminGroup!);

            return (IdentityAuthResult.Authenticated,
                new ExternalIdentity(samName.Trim(), display.Trim(), groups, isAdmin));
        }
        catch (LdapException ex)
        {
            log.LogWarning(ex, "LDAP authentication for '{User}' failed: {Code}", username, ex.ErrorCode);
            return (IdentityAuthResult.Unavailable, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unexpected LDAP error authenticating '{User}'.", username);
            return (IdentityAuthResult.Unavailable, null);
        }
    }

    private static string? GetAttr(SearchResultEntry entry, string name)
    {
        if (!entry.Attributes.Contains(name)) return null;
        var values = entry.Attributes[name].GetValues(typeof(string));
        return values.Length == 0 ? null : (string)values[0];
    }

    private static IReadOnlyList<string> GetAllAttr(SearchResultEntry entry, string name)
    {
        if (!entry.Attributes.Contains(name)) return Array.Empty<string>();
        var values = entry.Attributes[name].GetValues(typeof(string));
        var list = new List<string>(values.Length);
        foreach (var v in values) list.Add((string)v);
        return list;
    }

    /// <summary>
    /// Returns true when the user's <paramref name="groups"/> contain an entry whose CN
    /// or full DN matches <paramref name="want"/> (case-insensitive).
    /// </summary>
    public static bool MatchesGroup(IReadOnlyList<string> groups, string want)
    {
        if (string.IsNullOrWhiteSpace(want)) return false;
        var w = want.Trim();
        foreach (var dn in groups)
        {
            if (string.Equals(dn, w, StringComparison.OrdinalIgnoreCase)) return true;
            // Compare CN portion: "CN=Engineers,OU=Groups,DC=corp,DC=local" -> "Engineers"
            var cn = ExtractCn(dn);
            if (cn is not null && string.Equals(cn, w, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? ExtractCn(string dn)
    {
        var idx = dn.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = dn[(idx + 3)..];
        var comma = rest.IndexOf(',');
        return comma < 0 ? rest : rest[..comma];
    }

    /// <summary>RFC 4515 §3 escape for LDAP filter values.</summary>
    public static string EscapeLdapFilter(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
