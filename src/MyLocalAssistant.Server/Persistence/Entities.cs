namespace MyLocalAssistant.Server.Persistence;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = ""; // PBKDF2 string per Pbkdf2Hasher format
    public bool IsAdmin { get; set; }
    public bool MustChangePassword { get; set; }
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<UserRole> Roles { get; set; } = new();
    public List<UserDepartment> Departments { get; set; } = new();
    public List<RefreshToken> RefreshTokens { get; set; } = new();
}

public sealed class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<UserDepartment> Users { get; set; } = new();
}

public sealed class UserDepartment
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
}

public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<UserRole> Users { get; set; } = new();
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = ""; // SHA256 of opaque token
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class Agent
{
    /// <summary>Stable string id, e.g. "general-assistant".</summary>
    public string Id { get; set; } = "";
    /// <summary>Display name. For dept-bound agents, MUST equal the Department.Name to be visible to that dept.</summary>
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = ""; // Universal/Engineering/Business/Restricted
    public string SystemPrompt { get; set; } = "";
    /// <summary>Visible to all signed-in users when true; otherwise visible only to users in a department of the same Name (admins always see all).</summary>
    public bool IsGeneric { get; set; }
    /// <summary>Local-admin kill switch.</summary>
    public bool Enabled { get; set; }
    /// <summary>Optional per-agent model override; falls back to ModelManager active model when null.</summary>
    public string? DefaultModelId { get; set; }
    public bool RagEnabled { get; set; }
    /// <summary>Semicolon-separated list of RagCollection.Id (Guid) strings the agent retrieves from.</summary>
    public string? RagCollectionIds { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string AgentId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Message> Messages { get; set; } = new();
}

public enum MessageRole
{
    User = 1,
    Assistant = 2,
    System = 3,
}

public sealed class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public MessageRole Role { get; set; }
    /// <summary>Nullable so the retention job can purge body while keeping metadata.</summary>
    public string? Body { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? LatencyMs { get; set; }
    public string? ModelId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? BodyPurgedAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = ""; // e.g. "auth.login", "chat.send", "admin.user.create"
    public string? AgentId { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
    public bool Success { get; set; }
}

public sealed class RagCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Default Restricted: only principals listed in Grants (and admins) may read.</summary>
    public CollectionAccessMode AccessMode { get; set; } = CollectionAccessMode.Restricted;
    public List<RagDocument> Documents { get; set; } = new();
    public List<RagCollectionGrant> Grants { get; set; } = new();
}

public enum CollectionAccessMode
{
    /// <summary>Any authenticated user may read.</summary>
    Public = 0,
    /// <summary>Only principals in <see cref="RagCollection.Grants"/> may read. Admins always may read.</summary>
    Restricted = 1,
}

public enum PrincipalKind
{
    User = 1,
    Department = 2,
    Role = 3,
}

/// <summary>A single read grant on a RAG collection. Combination (CollectionId, PrincipalKind, PrincipalId) is unique.</summary>
public sealed class RagCollectionGrant
{
    public long Id { get; set; }
    public Guid CollectionId { get; set; }
    public RagCollection Collection { get; set; } = null!;
    public PrincipalKind PrincipalKind { get; set; }
    /// <summary>User.Id, Department.Id, or Role.Id depending on PrincipalKind.</summary>
    public Guid PrincipalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
}

public sealed class RagDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CollectionId { get; set; }
    public RagCollection Collection { get; set; } = null!;
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int ChunkCount { get; set; }
    public DateTimeOffset IngestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Sha256 { get; set; }
}
