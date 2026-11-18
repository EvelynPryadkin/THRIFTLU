using System.Security.Claims;
using LUThrift.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Authorization;

// Query the database each time; tracked users and role cookies may be stale.
// Keep these composable so services can also enforce access within their write/read SQL.
public static class CurrentAccountAccess
{
    public static string? GetUserId(ClaimsPrincipal? user)
    {
        var id = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        return user?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(id) && id.Length <= 450
            ? id : null;
    }

    public static IQueryable<string> ActiveAccounts(ApplicationDbContext context, string userId)
        => context.Users.AsNoTracking()
            .Where(account => account.Id == userId && account.IsActive)
            .Select(account => account.Id);

    public static IQueryable<string> StaffAccounts(ApplicationDbContext context, string userId)
        => from account in context.Users.AsNoTracking()
           join membership in context.UserRoles on account.Id equals membership.UserId
           join role in context.Roles on membership.RoleId equals role.Id
           where account.Id == userId && account.IsActive
               && (role.Name == ApplicationRoles.Staff || role.Name == ApplicationRoles.Administrator)
           select account.Id;

    public static IQueryable<string> AdministratorAccounts(ApplicationDbContext context, string userId)
        => from account in context.Users.AsNoTracking()
           join membership in context.UserRoles on account.Id equals membership.UserId
           join role in context.Roles on membership.RoleId equals role.Id
           where account.Id == userId && account.IsActive && role.Name == ApplicationRoles.Administrator
           select account.Id;
}
