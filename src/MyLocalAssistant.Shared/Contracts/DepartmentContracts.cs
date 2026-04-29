namespace MyLocalAssistant.Shared.Contracts;

public sealed record DepartmentDto(Guid Id, string Name, int UserCount);

public sealed record CreateDepartmentRequest(string Name);

public sealed record RenameDepartmentRequest(string Name);
