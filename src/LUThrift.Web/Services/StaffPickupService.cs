using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

public sealed class StaffPickupService(
    ApplicationDbContext context,
    ILookupNormalizer emailNormalizer,
    TimeProvider timeProvider)
{
    // Callers pass the authenticated server principal, never an identity from query fields.
    public async Task<StaffPickupQueryResult> GetPageAsync(
        StaffPickupQueryInput? input,
        ClaimsPrincipal? user,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actorId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450)
        {
            return new(StaffPickupQueryStatus.AuthenticationRequired, "Sign in to view staff pickups.");
        }

        // Cookie role claims can be stale. Check the current account and membership
        // before accessing reservation counts or projecting any student information.
        var access = from account in context.Users.AsNoTracking()
                     join membership in context.UserRoles on account.Id equals membership.UserId
                     join role in context.Roles on membership.RoleId equals role.Id
                     where account.Id == actorId && account.IsActive
                         && (role.Name == ApplicationRoles.Staff || role.Name == ApplicationRoles.Administrator)
                     select account.Id;
        if (!await access.AnyAsync(cancellationToken)) return StaffRequired();

        var validation = new List<ValidationResult>();
        if (input is null || !Validator.TryValidateObject(input, new ValidationContext(input), validation,
                validateAllProperties: true))
        {
            return new(StaffPickupQueryStatus.InvalidInput,
                validation.FirstOrDefault()?.ErrorMessage ?? "Enter valid pickup search options.");
        }

        string? normalizedEmail = null;
        if (input.StudentEmail is { } search)
        {
            try
            {
                // Match the same normalization used when Identity saves the account.
                normalizedEmail = emailNormalizer.NormalizeEmail(search);
            }
            catch (ArgumentException)
            {
                // Invalid Unicode must not reach the database provider.
                return InvalidEmail();
            }
            if (string.IsNullOrEmpty(normalizedEmail)
                || normalizedEmail.Length > StaffPickupQueryInput.MaximumEmailSearchLength)
                return InvalidEmail();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Keep the access predicate in both count and row SQL as well: revocation
        // between the initial gate and these statements must not disclose student data.
        var query = context.Reservations.AsNoTracking().Where(_ => access.Any());
        if (input.ReservationId is { } reservationId)
            query = query.Where(row => row.Id == reservationId);
        else
            query = query.Where(row => row.Status == ReservationStatus.Active && row.ExpiresAtUtc > now);

        if (normalizedEmail is not null)
        {
            // Contains is literal substring matching, including %, _ and !. Matching
            // normalized values keeps case handling out of database LIKE/collation rules.
            query = query.Where(row => row.User.NormalizedEmail != null
                && row.User.NormalizedEmail.Contains(normalizedEmail));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 1 : (totalCount - 1) / StaffPickupPage.PageSize + 1;
        var currentPage = Math.Clamp(pageNumber, 1, totalPages);
        var offset = (currentPage - 1) * StaffPickupPage.PageSize; // Clamp before multiplying.
        var items = await query.OrderBy(row => row.ExpiresAtUtc).ThenBy(row => row.Id)
            .Skip(offset).Take(StaffPickupPage.PageSize)
            .Select(row => new StaffPickupSummary(row.Id, row.Listing.Title,
                row.User.DisplayName, row.User.Email,
                row.Status == ReservationStatus.Active && row.ExpiresAtUtc <= now
                    ? ReservationStatus.Expired : row.Status,
                row.Listing.Status, row.ExpiresAtUtc))
            .ToListAsync(cancellationToken);

        // Do not return a page/count if access was revoked while the queries ran.
        if (!await access.AnyAsync(cancellationToken)) return StaffRequired();
        return new(StaffPickupQueryStatus.Success, string.Empty,
            new StaffPickupPage(items, totalCount, currentPage));
    }

    private static StaffPickupQueryResult StaffRequired()
        => new(StaffPickupQueryStatus.StaffRequired, "An active Staff or Administrator account is required.");

    private static StaffPickupQueryResult InvalidEmail()
        => new(StaffPickupQueryStatus.InvalidInput, "Enter valid email search text.");
}
