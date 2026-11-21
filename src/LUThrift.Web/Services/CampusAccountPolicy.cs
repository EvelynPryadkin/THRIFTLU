using System.Net.Mail;
using LUThrift.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

public sealed class CampusAccountPolicy
{
    public const string EmailSuffix = "@LAWRENCE.EDU";
    public bool CanRegister(string email)
        => email.Length <= 256 && MailAddress.TryCreate(email, out var address)
            && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)
            && string.Equals(address.Host, "lawrence.edu", StringComparison.OrdinalIgnoreCase);

    public static IQueryable<string> VerifiedAccounts(ApplicationDbContext context, string userId)
        => context.Users.AsNoTracking().Where(user => user.Id == userId && user.IsActive
            && user.EmailConfirmed && user.NormalizedEmail != null && user.NormalizedEmail.EndsWith(EmailSuffix))
            .Select(user => user.Id);
}
