using Microsoft.AspNetCore.Mvc;
using MyLocalAssistant.Server.Auth;
using MyLocalAssistant.Shared.Contracts;

namespace MyLocalAssistant.Server.Api;

public static class DepartmentEndpoints
{
    public static IEndpointRouteBuilder MapDepartmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/departments")
            .WithTags("Admin/Departments")
            .RequireAuthorization("Admin");

        group.MapGet("/", async (DepartmentService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        group.MapPost("/", async ([FromBody] CreateDepartmentRequest req, DepartmentService svc, CancellationToken ct) =>
        {
            var (dto, code) = await svc.CreateAsync(req.Name, ct);
            if (dto is not null) return Results.Created($"/api/admin/departments/{dto.Id}", dto);
            return code switch
            {
                ProblemCodes.Conflict => Problem(code, "A department with that name already exists.", StatusCodes.Status409Conflict),
                _ => Problem(code!, "Validation failed.", StatusCodes.Status400BadRequest),
            };
        });

        group.MapPatch("/{id:guid}", async (Guid id, [FromBody] RenameDepartmentRequest req, DepartmentService svc, CancellationToken ct) =>
        {
            var (dto, code) = await svc.RenameAsync(id, req.Name, ct);
            if (dto is not null) return Results.Ok(dto);
            return code switch
            {
                ProblemCodes.NotFound => Problem(code, "Department not found.", StatusCodes.Status404NotFound),
                ProblemCodes.Conflict => Problem(code, "A department with that name already exists.", StatusCodes.Status409Conflict),
                _ => Problem(code!, "Validation failed.", StatusCodes.Status400BadRequest),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, DepartmentService svc, CancellationToken ct) =>
        {
            var code = await svc.DeleteAsync(id, ct);
            return code is null
                ? Results.NoContent()
                : Problem(code, "Department not found.", StatusCodes.Status404NotFound);
        });

        return app;
    }

    private static IResult Problem(string code, string detail, int status) =>
        Results.Problem(detail: detail, statusCode: status, title: code, type: code);
}
