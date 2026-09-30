using LUThrift.Web.Authorization;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Identity;

namespace LUThrift.Web.Data;

public class RoleInitializer(
    RoleManager<IdentityRole> roleManager,
    UserManager<ApplicationUser> userManager,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<RoleInitializer> logger)
{
    public async Task InitializeAsync()
    {
        string[] roleNames = [ApplicationRoles.Student, ApplicationRoles.Staff, ApplicationRoles.Administrator];
        foreach (var roleName in roleNames)
        {
            if (await roleManager.RoleExistsAsync(roleName))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new IdentityRole(roleName));
            if (!result.Succeeded && !await roleManager.RoleExistsAsync(roleName))
            {
                throw new InvalidOperationException($"Could not initialize the {roleName} role.");
            }
        }

        // Never read or apply the administrator bootstrap setting outside Development.
        if (!environment.IsDevelopment())
        {
            return;
        }

        var email = configuration["DevelopmentAdmin:Email"];
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            logger.LogWarning(
                "Development administrator bootstrap skipped: the configured account does not exist. Register it first, then restart.");
            return;
        }

        if (await userManager.IsInRoleAsync(user, ApplicationRoles.Administrator))
        {
            return;
        }

        var assignment = await userManager.AddToRoleAsync(user, ApplicationRoles.Administrator);
        if (!assignment.Succeeded && !await userManager.IsInRoleAsync(user, ApplicationRoles.Administrator))
        {
            throw new InvalidOperationException("Could not assign the development administrator role.");
        }
    }
}
