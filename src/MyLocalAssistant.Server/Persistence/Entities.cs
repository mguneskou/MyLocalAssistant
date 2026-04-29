namespace MyLocalAssistant.Server.Persistence;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = ""; // PBKDF2 string per Pbkdf2Hasher format
    public string? Department { get; set; }
    public bool IsAdmin { get; set; }
    public bool MustChangePassword { get; set; }
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<UserRole> Roles { get; set; } = new();
    public List<RefreshToken> RefreshTokens { get; set; } = new();
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
    public string Id { get; set; } = ""; // stable string id, e.g. "general-assistant"
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = ""; // Universal/Engineering/Business/Restricted
    public string SystemPrompt { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Future: per-agent model override. v2.0 ignores this; falls back to ServerSettings.DefaultModelId.</summary>
    public string? DefaultModelId { get; set; }
    public bool RagEnabled { get; set; }
    public List<AgentAclRule> AclRules { get; set; } = new();
}

public enum AclSubjectKind
{
    User = 1,
    Role = 2,
    Department = 3,
}

public sealed class AgentAclRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AgentId { get; set; } = "";
    public Agent Agent { get; set; } = null!;
    public AclSubjectKind SubjectKind { get; set; }
    /// <summary>For User: User.Id as string. For Role/Department: the name.</summary>
    public string SubjectValue { get; set; } = "";
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
    public List<RagDocument> Documents { get; set; } = new();
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
