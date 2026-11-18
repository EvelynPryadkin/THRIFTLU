using LUThrift.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Authorization;

public sealed class CurrentAccountHandler(ApplicationDbContext database)
    : AuthorizationHandler<CurrentAccountRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, CurrentAccountRequirement requirement)
    {
        var userId = CurrentAccountAccess.GetUserId(context.User);
        if (userId is null) return;

        var accounts = requirement.Permission switch
        {
            CurrentAccountPermission.ActiveAccount => CurrentAccountAccess.ActiveAccounts(database, userId),
            CurrentAccountPermission.StaffOrAdministrator => CurrentAccountAccess.StaffAccounts(database, userId),
            CurrentAccountPermission.Administrator => CurrentAccountAccess.AdministratorAccounts(database, userId),
            _ => throw new InvalidOperationException("Unknown current-account permission.")
        };
        var cancellationToken = context.Resource switch
        {
            HttpContext http => http.RequestAborted,
            AuthorizationFilterContext filter => filter.HttpContext.RequestAborted,
            _ => CancellationToken.None
        };

        if (await accounts.AnyAsync(cancellationToken)) context.Succeed(requirement);
    }
}
