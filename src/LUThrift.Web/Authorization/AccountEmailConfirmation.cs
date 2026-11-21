using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Authorization;

// Existing accounts may continue to sign in. Newly registered accounts require
// confirmation. Reservation creation independently requires verified campus email.
public sealed class AccountEmailConfirmation(ApplicationDbContext context) : IUserConfirmation<ApplicationUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
        => context.Users.AsNoTracking().AnyAsync(account => account.Id == user.Id
            && (!account.EmailConfirmationRequiredForSignIn || account.EmailConfirmed));
}
