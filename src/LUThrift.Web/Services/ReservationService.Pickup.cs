using System.Data;
using System.Data.Common;
using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

public sealed partial class ReservationService
{
    // The caller supplies the authenticated server principal, never a submitted actor ID or role.
    public async Task<ReservationOperationResult> ConfirmPickupAsync(
        int reservationId, ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actorId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(actorId) || actorId.Length > 450)
            return new(ReservationOperationStatus.AuthenticationRequired, "Sign in to confirm a pickup.");

        try
        {
            // A stale staff cookie must not authorize even a lookup of private reservation data.
            if (!await EligiblePickupStaff(actorId).AnyAsync(cancellationToken)) return PickupStaffRequired();
            if (reservationId <= 0) return ReservationNotFound();
            return await ConfirmPickupInTransactionAsync(reservationId, actorId, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsDatabaseConflict(exception))
        {
            return PickupConflict();
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            // Provider exceptions can contain account data; keep logs and messages generic.
            logger.LogError("Reservation pickup could not be confirmed during persistence.");
            return new(ReservationOperationStatus.PersistenceFailed,
                "We couldn't confirm the pickup. Refresh before trying again.");
        }
    }

    private async Task<ReservationOperationResult> ConfirmPickupInTransactionAsync(
        int reservationId, string actorId, CancellationToken cancellationToken)
    {
        // Discover only the coordination keys here. Reread the reservation under both locks.
        var candidate = await context.Reservations.AsNoTracking().Where(row => row.Id == reservationId)
            .Select(row => new { row.UserId, row.ListingId }).SingleOrDefaultAsync(cancellationToken);
        if (candidate is null) return ReservationNotFound();

        var postgres = context.Database.IsNpgsql();
        if (!postgres && context.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            throw new NotSupportedException("Reservation coordination supports PostgreSQL and SQLite.");

        await using var transaction = await context.Database.BeginTransactionAsync(
            postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);

        // Lock the STUDENT owner, not the staff actor. Creation, cancellation and
        // expiration all use owner -> listing; changing this order could deadlock.
        var ownerExists = await LockUserAsync(candidate.UserId, postgres, cancellationToken);
        var listingStatus = await LockListingAsync(candidate.ListingId, postgres, cancellationToken);
        var staff = EligiblePickupStaff(actorId);
        if (!await staff.AnyAsync(cancellationToken)) return PickupStaffRequired();
        if (ownerExists is null || listingStatus is null) return ReservationNotFound();

        var reservation = await context.Reservations.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == reservationId && row.UserId == candidate.UserId && row.ListingId == candidate.ListingId,
            cancellationToken);
        if (reservation is null) return ReservationNotFound();
        if (reservation.Status != ReservationStatus.Active)
            return new(ReservationOperationStatus.NotActive, "This reservation is no longer active and cannot be collected.");

        // Pickup uses the saved deadline, not creation's full-hour schedule check.
        // No time is read until both potentially blocking locks have been acquired.
        var checkedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (reservation.ExpiresAtUtc <= checkedAtUtc) return PickupDeadlinePassed();
        if (listingStatus != ListingStatus.Held || reservation.FulfilledAtUtc is not null
            || reservation.CancellationReason is not null || reservation.CreatedAtUtc > checkedAtUtc
            || reservation.ExpiresAtUtc <= reservation.CreatedAtUtc)
            return PickupInvalidState();

        // Held alone cannot identify this reservation's hold. Match the lifecycle
        // release guard: another Active or chronologically newer record supersedes it.
        if (await context.Reservations.AnyAsync(other => other.ListingId == candidate.ListingId
                && other.Id != reservation.Id && (other.Status == ReservationStatus.Active
                    || other.CreatedAtUtc > reservation.CreatedAtUtc
                    || (other.CreatedAtUtc == reservation.CreatedAtUtc && other.Id > reservation.Id)), cancellationToken))
            return PickupInvalidState();

        if (!await staff.AnyAsync(cancellationToken)) return PickupStaffRequired();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (reservation.ExpiresAtUtc <= nowUtc) return PickupDeadlinePassed();
        if (nowUtc < checkedAtUtc) return PickupConflict();

        // Fresh time immediately before the transition; authorization is also in the
        // UPDATE predicate so a revocation committed since the last read cannot grant a write.
        var fulfilled = await context.Reservations.Where(row => row.Id == reservation.Id
                && row.UserId == candidate.UserId && row.ListingId == candidate.ListingId
                && row.Status == ReservationStatus.Active && row.FulfilledAtUtc == null && row.CancellationReason == null
                && row.CreatedAtUtc == reservation.CreatedAtUtc && row.ExpiresAtUtc == reservation.ExpiresAtUtc
                && row.ExpiresAtUtc > nowUtc && staff.Any())
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ReservationStatus.Fulfilled)
                .SetProperty(row => row.FulfilledAtUtc, nowUtc), cancellationToken);
        if (fulfilled != 1)
            return await staff.AnyAsync(cancellationToken) ? PickupConflict() : PickupStaffRequired();

        var collected = await context.Listings.Where(listing => listing.Id == candidate.ListingId
                && listing.Status == ListingStatus.Held
                && !context.Reservations.Any(other => other.ListingId == listing.Id && other.Id != reservation.Id
                    && (other.Status == ReservationStatus.Active || other.CreatedAtUtc > reservation.CreatedAtUtc
                        || (other.CreatedAtUtc == reservation.CreatedAtUtc && other.Id > reservation.Id))))
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, ListingStatus.Collected), cancellationToken);
        if (collected != 1) return PickupConflict();

        var audit = new AuditEvent
        {
            ListingId = candidate.ListingId,
            ActorUserId = actorId,
            Action = InventoryAuditActions.ReservationFulfilled,
            OccurredAtUtc = nowUtc,
            Details = $"Reservation {reservation.Id}: Active to Fulfilled; listing Held to Collected by staff pickup confirmation."
        };
        context.AuditEvents.Add(audit);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // A later SaveChanges on this scope must not retry a rolled-back audit.
            context.Entry(audit).State = EntityState.Detached;
            throw;
        }
        return new(ReservationOperationStatus.Fulfilled, "Pickup confirmed. The item has been collected.");
    }

    private IQueryable<string> EligiblePickupStaff(string actorId)
        => from account in context.Users.AsNoTracking()
           join membership in context.UserRoles on account.Id equals membership.UserId
           join role in context.Roles on membership.RoleId equals role.Id
           where account.Id == actorId && account.IsActive
               && (role.Name == ApplicationRoles.Staff || role.Name == ApplicationRoles.Administrator)
           select account.Id;

    private static ReservationOperationResult PickupStaffRequired()
        => new(ReservationOperationStatus.StaffRequired, "An active Staff or Administrator account is required.");

    private static ReservationOperationResult PickupDeadlinePassed()
        => new(ReservationOperationStatus.DeadlinePassed, "The pickup deadline has passed. This reservation cannot be collected.");

    private static ReservationOperationResult PickupInvalidState()
        => new(ReservationOperationStatus.InvalidState, "This reservation no longer has a matching held item. Refresh and review its status.");

    private static ReservationOperationResult PickupConflict()
        => new(ReservationOperationStatus.Conflict, "The reservation or item changed. Refresh and try again.");
}
