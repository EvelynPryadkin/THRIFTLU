using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Authorization;

public sealed class CurrentAccountSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> options,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    ApplicationDbContext database)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, options, logger, schemes, confirmation)
{
    // Identity uses this gate for password and two-factor sign-in. Read persisted
    // state so deactivation also blocks a sign-in begun with an older user instance.
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
        => await base.CanSignInAsync(user)
            && await CurrentAccountAccess.ActiveAccounts(database, user.Id).AnyAsync();

    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
    {
        // Identity's recovery-code path skips CanSignInAsync. Check before it
        // redeems a code or issues a cookie, including an in-progress sign-in.
        var user = await GetTwoFactorAuthenticationUserAsync();
        if (user is null) return SignInResult.Failed;
        if (!await CanSignInAsync(user)) return SignInResult.NotAllowed;
        return await base.TwoFactorRecoveryCodeSignInAsync(recoveryCode);
    }
}
