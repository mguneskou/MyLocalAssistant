using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MyLocalAssistant.Server.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<UserDepartment> UserDepartments => Set<UserDepartment>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<RagCollection> RagCollections => Set<RagCollection>();
    public DbSet<RagDocument> RagDocuments => Set<RagDocument>();
    public DbSet<RagCollectionGrant> RagCollectionGrants => Set<RagCollectionGrant>();
    public DbSet<ToolState> Tools => Set<ToolState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // SQLite has no native datetime type and EF Core refuses to translate ORDER BY
        // on DateTimeOffset (the default TEXT representation isn't lexically sortable
        // because of timezone offsets). Store all timestamps as UTC ticks (long) so
        // ORDER BY works in SQL. Round-trips lossless for DateTimeOffset (offset is
        // dropped — we always treat persisted timestamps as UTC).
        var dtoConverter = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
        var nullableDtoConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : (long?)null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : (DateTimeOffset?)null);
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTimeOffset)) prop.SetValueConverter(dtoConverter);
                else if (prop.ClrType == typeof(DateTimeOffset?)) prop.SetValueConverter(nullableDtoConverter);
            }
        }

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(x => x.AuthSource).HasMaxLength(16).IsRequired();
            e.Property(x => x.WorkRoot).HasMaxLength(512);
        });

        b.Entity<Department>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
        });

        b.Entity<UserDepartment>(e =>
        {
            e.HasKey(x => new { x.UserId, x.DepartmentId });
            e.HasOne(x => x.User).WithMany(x => x.Departments).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Department).WithMany(x => x.Users).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
        });

        b.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(x => x.Roles).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany(x => x.Users).HasForeignKey(x => x.RoleId);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId);
        });

        b.Entity<Agent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.SystemPrompt).HasMaxLength(8192);
            e.Property(x => x.RagCollectionIds).HasMaxLength(2048);
            e.Property(x => x.ScenarioNotes).HasMaxLength(4096);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Conversation>(e =>
        {
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
        });

        b.Entity<Message>(e =>
        {
            e.HasOne(x => x.Conversation).WithMany(x => x.Messages)
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
        });

        b.Entity<RagCollection>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        b.Entity<RagDocument>(e =>
        {
            e.HasOne(x => x.Collection).WithMany(x => x.Documents).HasForeignKey(x => x.CollectionId);
            e.HasIndex(x => new { x.CollectionId, x.FileName });
        });

        b.Entity<RagCollectionGrant>(e =>
        {
            e.HasOne(x => x.Collection).WithMany(x => x.Grants)
                .HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
            // Defense-in-depth: prevent duplicate grants for the same principal.
            e.HasIndex(x => new { x.CollectionId, x.PrincipalKind, x.PrincipalId }).IsUnique();
            e.HasIndex(x => new { x.PrincipalKind, x.PrincipalId });
        });

        b.Entity<ToolState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(16).IsRequired();
            e.Property(x => x.InstalledVersion).HasMaxLength(32);
            // ConfigJson is intentionally unbounded; admin sees raw JSON in the editor.
        });
    }
}
