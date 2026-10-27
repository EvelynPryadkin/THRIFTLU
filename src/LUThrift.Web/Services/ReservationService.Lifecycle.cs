using System.Data;
using System.Data.Common;
using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Models;
using LUThrift.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

public sealed partial class ReservationService
{
    // Audit actors are historical scalar IDs. Expiration is a system operation,
    // not an action impersonating the student whose reservation expired.
    public const string ExpirationActorId = "system:reservation-expiration";

    public Task<ReservationOperationResult> CancelAsync(
        int reservationId, ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId) || userId.Length > 450)
        {
            return Task.FromResult(new ReservationOperationResult(ReservationOperationStatus.AuthenticationRequired,
                "Sign in to manage your reservations."));
        }
        return TransitionSafelyAsync(reservationId, userId, cancellationToken);
    }

    public Task<ReservationOperationResult> ExpireAsync(
        int reservationId, CancellationToken cancellationToken = default)
        => TransitionSafelyAsync(reservationId, null, cancellationToken);

    public async Task<ReservationExpirationBatchResult> ExpireBatchAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ids = await context.Reservations.AsNoTracking()
            .Where(row => row.Status == ReservationStatus.Active && row.ExpiresAtUtc <= now)
            .OrderBy(row => row.ExpiresAtUtc).ThenBy(row => row.Id)
            .Select(row => row.Id).Take(Math.Clamp(batchSize, 1, ReservationOptions.MaximumExpirationBatchSize))
            .ToListAsync(cancellationToken);
        var processed = 0;
        var failed = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExpireAsync(id, cancellationToken);
            if (result.Changed) processed++;
            if (result.Status is ReservationOperationStatus.PersistenceFailed or ReservationOperationStatus.Conflict) failed++;
        }
        return new(processed, failed);
    }

    public async Task<ReservationOverview> GetForUserAsync(
        ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        if (!await IsEligibleStudentAsync(user, cancellationToken))
        {
            return new([], []);
        }
        var userId = user!.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var owned = context.Reservations.AsNoTracking().Where(row => row.UserId == userId);
        var current = owned.Where(row => row.Status == ReservationStatus.Active && row.ExpiresAtUtc > now);
        var history = owned.Where(row => row.Status != ReservationStatus.Active || row.ExpiresAtUtc <= now)
            .OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id)
            .Take(ReservationOverview.RecentHistoryLimit);

        // One SQL statement keeps the lists and count in the same database snapshot.
        // History is bounded in SQL independently of current reservations.
        var rows = await current.Concat(history)
            .OrderByDescending(row => row.Status == ReservationStatus.Active && row.ExpiresAtUtc > now)
            .ThenByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id)
            .Select(row => new ReservationSummary(row.Id, row.ListingId, row.Listing.Title,
                row.CreatedAtUtc, row.ExpiresAtUtc,
                row.Status == ReservationStatus.Active && row.ExpiresAtUtc <= now ? ReservationStatus.Expired : row.Status,
                row.Status == ReservationStatus.Active && row.ExpiresAtUtc > now))
            .ToListAsync(cancellationToken);
        return new(rows.Where(row => row.CanCancel).ToArray(), rows.Where(row => !row.CanCancel).ToArray());
    }

    private async Task ReconcileListingAsync(int listingId, CancellationToken cancellationToken)
    {
        var candidate = await context.Reservations.AsNoTracking()
            .Where(row => row.ListingId == listingId && row.Status == ReservationStatus.Active)
            .OrderBy(row => row.Id).Select(row => new { row.Id, row.ExpiresAtUtc }).FirstOrDefaultAsync(cancellationToken);
        if (candidate is not null && candidate.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
        {
            // Throw persistence failures to CreateAsync's safe result handler. A failed
            // reconciliation must not be mistaken for a successfully released hold.
            await TransitionInTransactionAsync(candidate.Id, null, cancellationToken);
        }
    }

    private async Task<ReservationOperationResult> TransitionSafelyAsync(
        int reservationId, string? cancelingUserId, CancellationToken cancellationToken)
    {
        try
        {
            return await TransitionInTransactionAsync(reservationId, cancelingUserId, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsDatabaseConflict(exception))
        {
            return new(ReservationOperationStatus.Conflict, "The reservation changed. Refresh and try again.");
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError("A reservation transition could not be confirmed during persistence.");
            return new(ReservationOperationStatus.PersistenceFailed,
                "We couldn't confirm the change. Refresh before trying again.");
        }
    }

    private async Task<ReservationOperationResult> TransitionInTransactionAsync(
        int reservationId, string? cancelingUserId, CancellationToken cancellationToken)
    {
        // This lookup only discovers lock keys, never authorizes a later write.
        // Unknown and other-owner IDs deliberately follow the same not-found path.
        var candidate = await context.Reservations.AsNoTracking()
            .Where(row => row.Id == reservationId && (cancelingUserId == null || row.UserId == cancelingUserId))
            .Select(row => new { row.UserId, row.ListingId }).SingleOrDefaultAsync(cancellationToken);
        if (candidate is null) return ReservationNotFound();

        var postgres = context.Database.IsNpgsql();
        if (!postgres && context.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            throw new NotSupportedException("Reservation coordination supports PostgreSQL and SQLite.");
        await using var transaction = await context.Database.BeginTransactionAsync(
            postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);

        var activeUser = await LockUserAsync(candidate.UserId, postgres, cancellationToken);
        await LockListingAsync(candidate.ListingId, postgres, cancellationToken);
        if (cancelingUserId is not null && (activeUser != true || !await IsStudentAsync(cancelingUserId, cancellationToken)))
            return new(ReservationOperationStatus.StudentRequired, "An active Student account is required.");

        var reservation = await context.Reservations.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == reservationId && row.UserId == candidate.UserId && row.ListingId == candidate.ListingId,
            cancellationToken);
        if (reservation is null) return ReservationNotFound();
        if (reservation.Status != ReservationStatus.Active)
            return new(ReservationOperationStatus.NotActive, "This reservation is no longer active.");

        // Expiration does not depend on pickup hours or account eligibility.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = reservation.ExpiresAtUtc <= now;
        if (!expired && cancelingUserId is null)
            return new(ReservationOperationStatus.NotDue, "This reservation has not reached its deadline.");
        var nextStatus = expired ? ReservationStatus.Expired : ReservationStatus.Canceled;
        var changed = await context.Reservations.Where(row => row.Id == reservation.Id
                && row.Status == ReservationStatus.Active && row.UserId == candidate.UserId
                && row.ListingId == candidate.ListingId && row.ExpiresAtUtc == reservation.ExpiresAtUtc)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, nextStatus)
                .SetProperty(row => row.CancellationReason, expired ? null : "Canceled by student."), cancellationToken);
        if (changed != 1)
            return new(ReservationOperationStatus.Conflict, "The reservation changed. Refresh and try again.");

        // A later reservation or any other stored Active row protects its hold.
        // Checking only Held would let delayed cleanup release a newer reservation.
        var released = await context.Listings.Where(listing => listing.Id == candidate.ListingId
                && listing.Status == ListingStatus.Held
                && !context.Reservations.Any(other => other.ListingId == listing.Id && other.Id != reservation.Id
                    && (other.Status == ReservationStatus.Active || other.CreatedAtUtc > reservation.CreatedAtUtc
                        || (other.CreatedAtUtc == reservation.CreatedAtUtc && other.Id > reservation.Id))))
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, ListingStatus.Available), cancellationToken);

        var audit = new AuditEvent
        {
            ListingId = candidate.ListingId,
            ActorUserId = expired ? ExpirationActorId : candidate.UserId,
            Action = expired ? InventoryAuditActions.ReservationExpired : InventoryAuditActions.ReservationCanceled,
            OccurredAtUtc = now,
            Details = $"Reservation {reservation.Id} {(expired ? "expired" : "canceled")}; "
                + (released == 1 ? "hold released." : "inventory unchanged.")
        };
        context.AuditEvents.Add(audit);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            context.Entry(audit).State = EntityState.Detached;
            throw;
        }
        return expired
            ? new(ReservationOperationStatus.Expired, "The reservation has expired.")
            : new(ReservationOperationStatus.Canceled, "Reservation canceled.");
    }

    private Task<bool> IsStudentAsync(string userId, CancellationToken cancellationToken)
        => (from membership in context.UserRoles
            join role in context.Roles on membership.RoleId equals role.Id
            where membership.UserId == userId && role.Name == ApplicationRoles.Student
            select membership).AnyAsync(cancellationToken);

    private static ReservationOperationResult ReservationNotFound()
        => new(ReservationOperationStatus.NotFound, "The reservation could not be found.");
}
