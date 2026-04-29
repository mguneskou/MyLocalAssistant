using Microsoft.EntityFrameworkCore;
using MyLocalAssistant.Server.Persistence;

namespace MyLocalAssistant.Server.Rag;

/// <summary>Resolved authorization principals for a single request. Always loaded from the database (never trusted from JWT) so revocations take effect immediately.</summary>
public sealed record UserPrincipals(
    Guid UserId,
    string? Username,
    bool IsAdmin,
    IReadOnlySet<Guid> DepartmentIds,
    IReadOnlySet<Guid> RoleIds)
{
    public static readonly UserPrincipals Anonymous = new(Guid.Empty, null, false,
        new HashSet<Guid>(), new HashSet<Guid>());
}

/// <summary>
/// Single source of truth for "may this user read this RAG collection?".
/// Default-deny: a Restricted collection with no matching grant is unreadable except by admins.
/// </summary>
public sealed class RagAuthorizationService(AppDbContext db, ILogger<RagAuthorizationService> log)
{
    /// <summary>Loads roles + departments fresh from the DB. Use once per request.</summary>
    public async Task<UserPrincipals> ResolveAsync(Guid userId, string? username, bool isAdmin, CancellationToken ct)
    {
        if (userId == Guid.Empty) return UserPrincipals.Anonymous;
        var deptIds = await db.UserDepartments
            .Where(ud => ud.UserId == userId)
            .Select(ud => ud.DepartmentId)
            .ToListAsync(ct);
        var roleIds = await db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);
        return new UserPrincipals(userId, username, isAdmin, deptIds.ToHashSet(), roleIds.ToHashSet());
    }

    /// <summary>True iff the principal may read the collection. Admins always pass.</summary>
    public bool CanRead(UserPrincipals principal, RagCollection collection, IReadOnlyList<RagCollectionGrant> grants)
    {
        if (principal.IsAdmin) return true;
        if (collection.AccessMode == CollectionAccessMode.Public) return true;
        if (principal.UserId == Guid.Empty) return false;
        // Restricted: must have a matching grant.
        foreach (var g in grants)
        {
            if (g.CollectionId != collection.Id) continue;
            switch (g.PrincipalKind)
            {
                case PrincipalKind.User when g.PrincipalId == principal.UserId: return true;
                case PrincipalKind.Department when principal.DepartmentIds.Contains(g.PrincipalId): return true;
                case PrincipalKind.Role when principal.RoleIds.Contains(g.PrincipalId): return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Filters the requested collection ids to those the principal may read.
    /// Performs a single DB roundtrip; returns (allowed, denied) tuple.
    /// </summary>
    public async Task<AuthorizationDecision> AuthorizeReadAsync(
        UserPrincipals principal,
        IReadOnlyCollection<Guid> requestedCollectionIds,
        CancellationToken ct)
    {
        if (requestedCollectionIds.Count == 0)
            return new AuthorizationDecision(Array.Empty<Guid>(), Array.Empty<Guid>());

        var collections = await db.RagCollections
            .Where(c => requestedCollectionIds.Contains(c.Id))
            .Select(c => new { c.Id, c.AccessMode })
            .ToListAsync(ct);

        var grants = principal.IsAdmin
            ? new List<RagCollectionGrant>()
            : await db.RagCollectionGrants
                .Where(g => requestedCollectionIds.Contains(g.CollectionId))
                .ToListAsync(ct);

        var allowed = new List<Guid>();
        var denied = new List<Guid>();
        foreach (var rid in requestedCollectionIds)
        {
            var c = collections.FirstOrDefault(x => x.Id == rid);
            if (c is null) { denied.Add(rid); continue; } // unknown id: treat as denied
            var stub = new RagCollection { Id = c.Id, AccessMode = c.AccessMode };
            if (CanRead(principal, stub, grants)) allowed.Add(rid);
            else denied.Add(rid);
        }

        if (denied.Count > 0)
        {
            log.LogWarning("RAG authorize: user {User} denied {DeniedCount} collection(s) of {Total}.",
                principal.Username ?? principal.UserId.ToString(), denied.Count, requestedCollectionIds.Count);
        }
        return new AuthorizationDecision(allowed, denied);
    }

    /// <summary>Lists every collection the principal may read. Used by user-facing list endpoints.</summary>
    public async Task<List<RagCollection>> ListReadableAsync(UserPrincipals principal, CancellationToken ct)
    {
        var all = await db.RagCollections.AsNoTracking().ToListAsync(ct);
        if (principal.IsAdmin) return all;
        var ids = all.Select(c => c.Id).ToList();
        var grants = await db.RagCollectionGrants
            .Where(g => ids.Contains(g.CollectionId))
            .ToListAsync(ct);
        return all.Where(c => CanRead(principal, c, grants)).ToList();
    }
}

public sealed record AuthorizationDecision(IReadOnlyList<Guid> Allowed, IReadOnlyList<Guid> Denied);
