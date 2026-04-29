namespace MyLocalAssistant.Shared.Contracts;

public sealed record DepartmentDto(Guid Id, string Name, int UserCount);

public sealed record RoleDto(Guid Id, string Name);
