using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Endpoints;

public static class ModuleAccessEndpoints
{
    public static RouteGroupBuilder MapModuleAccessEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/module-access/catalog", () =>
            ApplicationModuleCatalog.All.Select(module =>
                new ModuleAccessCatalogEntryDto(
                    module.Key,
                    module.Name,
                    module.Roles.Select(role =>
                        new ModuleAccessRoleDto(
                            role.Role,
                            role.Permissions.Select(ToPermissionDto).ToList()))
                        .ToList()))
                .ToList())
            .RequireAuthorization("ManageUsers");

        api.MapGet("/admin/module-access", async (
            ProjectTrackerDbContext db,
            CancellationToken cancellationToken) =>
        {
            var users = await db.Users
                .AsNoTracking()
                .Include(user => user.ModuleAccessAssignments)
                .OrderByDescending(user => user.IsActive)
                .ThenBy(user => user.DisplayName)
                .ThenBy(user => user.AccountName)
                .ToListAsync(cancellationToken);

            return users.Select(user =>
                new ModuleAccessUserDto(
                    user.Id,
                    user.AccountName,
                    user.DisplayName,
                    user.IsActive,
                    ApplicationModuleCatalog.All
                        .Select(module => ToModuleAccessDto(
                            module,
                            user.ModuleAccessAssignments.FirstOrDefault(
                                access => access.ModuleKey == module.Key)))
                        .ToList()))
                .ToList();
        }).RequireAuthorization("ManageUsers");

        api.MapPut("/admin/users/{id:int}/module-access/{moduleKey}", async (
            int id,
            string moduleKey,
            ModuleAccessUpdateDto dto,
            ProjectTrackerDbContext db,
            ModuleAccessService moduleAccess,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var assignment = await moduleAccess.SetAsync(
                    db,
                    id,
                    moduleKey,
                    dto.Enabled,
                    dto.Role,
                    cancellationToken);
                var module = ApplicationModuleCatalog.Find(assignment.ModuleKey)!;
                return Results.Ok(ToModuleAccessDto(module, assignment));
            }
            catch (ModuleAccessValidationException exception)
            {
                return Results.BadRequest(new
                {
                    code = "InvalidModuleAccess",
                    message = exception.Message
                });
            }
            catch (ModuleAccessUserNotFoundException)
            {
                return Results.NotFound();
            }
            catch (LastModuleAdministratorException exception)
            {
                return Results.Conflict(new
                {
                    code = "LastModuleAdministrator",
                    message = exception.Message,
                    moduleKey = exception.ModuleKey
                });
            }
        }).RequireAuthorization("ManageUsers");

        return api;
    }

    private static UserModuleAccessDto ToModuleAccessDto(
        ApplicationModuleDefinition module,
        Models.AppUserModuleAccess? assignment)
    {
        var role = ApplicationModuleRoles.Normalize(assignment?.Role);
        return new UserModuleAccessDto(
            module.Key,
            role is not null,
            role,
            role is null
                ? []
                : ApplicationModuleCatalog.PermissionsFor(module.Key, role)
                    .Select(permission => permission.Key)
                    .ToList(),
            assignment?.UpdatedAt);
    }

    private static PermissionDefinitionDto ToPermissionDto(PermissionDefinition permission) =>
        new(permission.Key, permission.Label, permission.Description, permission.Category);
}
