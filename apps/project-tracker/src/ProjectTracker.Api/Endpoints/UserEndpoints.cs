using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", async (CurrentUserService currentUser, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(user => user.AccountName == currentUser.AccountName, cancellationToken);
            if (user is null)
            {
                user = new AppUser
                {
                    AccountName = currentUser.AccountName,
                    DisplayName = currentUser.DisplayName,
                    Role = currentUser.Role
                };
                db.Users.Add(user);
            }

            user.LastSeenAt = DateTimeOffset.UtcNow;
            user.Role = currentUser.Role;
            await db.SaveChangesAsync(cancellationToken);
            return new UserDto(currentUser.AccountName, currentUser.DisplayName, currentUser.Role, currentUser.CanEdit, currentUser.IsAdmin);
        });

        api.MapGet("/admin/users", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            return await db.Users
                .AsNoTracking()
                .OrderBy(user => user.Role == ApplicationRoles.Admin ? 0 : user.Role == ApplicationRoles.Editor ? 1 : 2)
                .ThenBy(user => user.DisplayName)
                .ThenBy(user => user.AccountName)
                .Select(user => new AdminUserDto(user.Id, user.AccountName, user.DisplayName, user.Role, user.LastSeenAt))
                .ToListAsync(cancellationToken);
        }).RequireAuthorization("AdminOnly");

        api.MapPut("/admin/users/{id:int}/role", async (int id, UserRoleUpdateDto dto, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            var role = ApplicationRoles.Normalize(dto.Role);
            if (role is null)
            {
                return Results.BadRequest("Role must be Admin, Editor, or Viewer.");
            }

            var user = await db.Users.FindAsync([id], cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase)
                && role != ApplicationRoles.Admin
                && await db.Users.CountAsync(candidate => candidate.Role == ApplicationRoles.Admin, cancellationToken) <= 1)
            {
                return Results.BadRequest("At least one administrator must remain assigned.");
            }

            user.Role = role;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new AdminUserDto(user.Id, user.AccountName, user.DisplayName, user.Role, user.LastSeenAt));
        }).RequireAuthorization("AdminOnly");

        return api;
    }
}
