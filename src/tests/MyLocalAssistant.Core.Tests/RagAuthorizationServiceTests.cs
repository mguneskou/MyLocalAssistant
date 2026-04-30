using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyLocalAssistant.Server.Persistence;
using MyLocalAssistant.Server.Rag;

namespace MyLocalAssistant.Core.Tests;

public class RagAuthorizationServiceTests
{
    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase("rag-authz-" + Guid.NewGuid()).Options);

    private static UserPrincipals U(Guid uid, bool admin = false, IEnumerable<Guid>? depts = null, IEnumerable<Guid>? roles = null)
        => new(uid, "u", admin, false, (depts ?? Array.Empty<Guid>()).ToHashSet(), (roles ?? Array.Empty<Guid>()).ToHashSet());

    [Fact]
    public void CanRead_PublicCollection_AnyAuthenticatedUser()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var c = new RagCollection { Id = Guid.NewGuid(), AccessMode = CollectionAccessMode.Public };
        Assert.True(svc.CanRead(U(Guid.NewGuid()), c, Array.Empty<RagCollectionGrant>()));
    }

    [Fact]
    public void CanRead_RestrictedCollection_NoGrant_DeniedForNonAdmin()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var c = new RagCollection { Id = Guid.NewGuid(), AccessMode = CollectionAccessMode.Restricted };
        Assert.False(svc.CanRead(U(Guid.NewGuid()), c, Array.Empty<RagCollectionGrant>()));
    }

    [Fact]
    public void CanRead_RestrictedCollection_AdminAlwaysAllowed()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var c = new RagCollection { Id = Guid.NewGuid(), AccessMode = CollectionAccessMode.Restricted };
        Assert.True(svc.CanRead(U(Guid.NewGuid(), admin: true), c, Array.Empty<RagCollectionGrant>()));
    }

    [Fact]
    public void CanRead_GrantByUserId_Allowed()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var uid = Guid.NewGuid();
        var c = new RagCollection { Id = Guid.NewGuid(), AccessMode = CollectionAccessMode.Restricted };
        var grants = new[] { new RagCollectionGrant { CollectionId = c.Id, PrincipalKind = PrincipalKind.User, PrincipalId = uid } };
        Assert.True(svc.CanRead(U(uid), c, grants));
        Assert.False(svc.CanRead(U(Guid.NewGuid()), c, grants));
    }

    [Fact]
    public void CanRead_GrantByDepartment_Allowed()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var dept = Guid.NewGuid();
        var c = new RagCollection { Id = Guid.NewGuid(), AccessMode = CollectionAccessMode.Restricted };
        var grants = new[] { new RagCollectionGrant { CollectionId = c.Id, PrincipalKind = PrincipalKind.Department, PrincipalId = dept } };
        Assert.True(svc.CanRead(U(Guid.NewGuid(), depts: new[] { dept }), c, grants));
        Assert.False(svc.CanRead(U(Guid.NewGuid()), c, grants));
    }

    [Fact]
    public void CanRead_GrantByRole_Allowed()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var role = Guid.NewGuid();
        var c = new RagCollection { Id = Guid.NewGuid(), AccessMode = CollectionAccessMode.Restricted };
        var grants = new[] { new RagCollectionGrant { CollectionId = c.Id, PrincipalKind = PrincipalKind.Role, PrincipalId = role } };
        Assert.True(svc.CanRead(U(Guid.NewGuid(), roles: new[] { role }), c, grants));
        Assert.False(svc.CanRead(U(Guid.NewGuid()), c, grants));
    }

    [Fact]
    public void CanRead_GrantOnDifferentCollection_Ignored()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var uid = Guid.NewGuid();
        var c = new RagCollection { Id = Guid.NewGuid(), AccessMode = CollectionAccessMode.Restricted };
        var grants = new[] { new RagCollectionGrant { CollectionId = Guid.NewGuid(), PrincipalKind = PrincipalKind.User, PrincipalId = uid } };
        Assert.False(svc.CanRead(U(uid), c, grants));
    }

    [Fact]
    public void CanRead_AnonymousNeverPasses_RestrictedCollection()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var c = new RagCollection { Id = Guid.NewGuid(), AccessMode = CollectionAccessMode.Restricted };
        Assert.False(svc.CanRead(UserPrincipals.Anonymous, c, Array.Empty<RagCollectionGrant>()));
    }

    [Fact]
    public async Task AuthorizeReadAsync_PartitionsAllowedAndDenied()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var pub = new RagCollection { Id = Guid.NewGuid(), Name = "pub", AccessMode = CollectionAccessMode.Public };
        var sec = new RagCollection { Id = Guid.NewGuid(), Name = "sec", AccessMode = CollectionAccessMode.Restricted };
        db.RagCollections.AddRange(pub, sec);
        await db.SaveChangesAsync();

        var user = U(Guid.NewGuid());
        var decision = await svc.AuthorizeReadAsync(user, new[] { pub.Id, sec.Id }, default);
        Assert.Single(decision.Allowed);
        Assert.Single(decision.Denied);
        Assert.Equal(pub.Id, decision.Allowed[0]);
        Assert.Equal(sec.Id, decision.Denied[0]);
    }

    [Fact]
    public async Task AuthorizeReadAsync_UnknownIdsAreDenied()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var phantom = Guid.NewGuid();
        var user = U(Guid.NewGuid());
        var decision = await svc.AuthorizeReadAsync(user, new[] { phantom }, default);
        Assert.Empty(decision.Allowed);
        Assert.Single(decision.Denied);
    }

    [Fact]
    public async Task AuthorizeReadAsync_AdminGetsEverything()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var sec1 = new RagCollection { Id = Guid.NewGuid(), Name = "s1", AccessMode = CollectionAccessMode.Restricted };
        var sec2 = new RagCollection { Id = Guid.NewGuid(), Name = "s2", AccessMode = CollectionAccessMode.Restricted };
        db.RagCollections.AddRange(sec1, sec2);
        await db.SaveChangesAsync();
        var admin = U(Guid.NewGuid(), admin: true);
        var decision = await svc.AuthorizeReadAsync(admin, new[] { sec1.Id, sec2.Id }, default);
        Assert.Equal(2, decision.Allowed.Count);
        Assert.Empty(decision.Denied);
    }

    [Fact]
    public async Task ResolveAsync_LoadsRolesAndDepartmentsFromDb()
    {
        using var db = NewDb();
        var svc = new RagAuthorizationService(db, NullLogger<RagAuthorizationService>.Instance);
        var user = new User { Username = "alice", DisplayName = "Alice" };
        var dept = new Department { Name = "HR" };
        var role = new Role { Name = "auditor" };
        db.Users.Add(user); db.Departments.Add(dept); db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.UserDepartments.Add(new UserDepartment { UserId = user.Id, DepartmentId = dept.Id });
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var p = await svc.ResolveAsync(user.Id, "alice", isAdminHint: false, default);
        Assert.False(p.IsAdmin);
        Assert.Single(p.DepartmentIds);
        Assert.Contains(dept.Id, p.DepartmentIds);
        Assert.Single(p.RoleIds);
        Assert.Contains(role.Id, p.RoleIds);
    }
}
