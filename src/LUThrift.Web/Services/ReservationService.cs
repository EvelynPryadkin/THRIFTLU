using System.Data;
using System.Data.Common;
using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LUThrift.Web.Services;

public sealed partial class ReservationService(
    ApplicationDbContext context,
    PickupHoursEligibility pickupHours,
    ILogger<ReservationService> logger,
    TimeProvider timeProvider)
{
    // A read-only hint for public UI; creation still rechecks eligibility under locks.
    public async Task<bool> IsEligibleStudentAsync(
        ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        return user?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(userId) && userId.Length <= 450
            && await context.Users.AnyAsync(row => row.Id == userId && row.IsActive, cancellationToken)
            && await IsStudentAsync(userId, cancellationToken);
    }

    // The PageModel must pass its authenticated User, never a principal constructed
    // from submitted fields. Role claims alone do not establish current eligibility.
    public async Task<ReservationCreationResult> CreateAsync(
        CreateReservationInput? input, ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId) || userId.Length > 450)
        {
            return Failure(ReservationCreationStatus.AuthenticationRequired, "Sign in to reserve an item.");
        }

        if (input is null || input.ListingId <= 0)
        {
            return Failure(ReservationCreationStatus.InvalidInput, "Choose a valid listing.");
        }

        try
        {
            // Reject ineligible callers before allowing reconciliation to write.
            // The account and membership are checked again under creation's locks.
            if (!await IsEligibleStudentAsync(user, cancellationToken))
            {
                return Failure(ReservationCreationStatus.StudentRequired, "An active Student account is required.");
            }
            // Reconciliation owns a separate owner→listing transaction. Never acquire
            // an old owner's lock while already holding the new student's/listing locks.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await ReconcileListingAsync(input.ListingId, cancellationToken);
                var result = await CreateInTransactionAsync(input.ListingId, userId, cancellationToken);
                if (result.Status != ReservationCreationStatus.Unavailable || attempt == 1)
                {
                    return result;
                }
                // A hold may have crossed its deadline while creation waited for locks.
            }
            return Conflict();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict();
        }
        catch (Exception exception) when (IsDatabaseConflict(exception))
        {
            return Conflict();
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            // Database exception details can contain account or submitted data.
            logger.LogError("Reservation creation could not be confirmed during persistence.");
            return Failure(ReservationCreationStatus.PersistenceFailed,
                "We couldn't confirm the reservation. Refresh before trying again.");
        }
    }

    private async Task<ReservationCreationResult> CreateInTransactionAsync(
        int listingId, string userId, CancellationToken cancellationToken)
    {
        var postgres = context.Database.IsNpgsql();
        if (!postgres && context.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            throw new NotSupportedException("Reservation coordination supports PostgreSQL and SQLite.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(
            postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);

        // All reservation creation paths acquire locks in the same order: user, listing.
        // The user lock serializes the active-count check across different listings.
        var isActiveUser = await LockUserAsync(userId, postgres, cancellationToken);
        if (isActiveUser != true)
        {
            return Failure(ReservationCreationStatus.StudentRequired, "An active Student account is required.");
        }

        var status = await LockListingAsync(listingId, postgres, cancellationToken);
        if (!await (
                from membership in context.UserRoles
                join role in context.Roles on membership.RoleId equals role.Id
                where membership.UserId == userId && role.Name == ApplicationRoles.Student
                select membership).AnyAsync(cancellationToken))
        {
            return Failure(ReservationCreationStatus.StudentRequired, "An active Student account is required.");
        }

        if (status is null)
        {
            return Failure(ReservationCreationStatus.NotFound, "The item could not be found.");
        }

        // No reservation time is captured before either potentially blocking lock.
        var window = pickupHours.Evaluate();
        if (!window.IsEligible)
        {
            return PickupUnavailable(window.Status);
        }
        var countedAtUtc = window.StartsAtUtc!.Value.UtcDateTime;

        var existing = await context.Reservations.AsNoTracking()
            .Where(reservation => reservation.ListingId == listingId && reservation.Status == ReservationStatus.Active)
            .Select(reservation => new { reservation.UserId, reservation.ExpiresAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing?.UserId == userId && existing.ExpiresAtUtc > countedAtUtc)
        {
            return Failure(ReservationCreationStatus.AlreadyReserved, "You already have a reservation for this item.");
        }
        if (status != ListingStatus.Available || existing is not null)
        {
            // Reconciliation must commit first; never overwrite a stored Active row.
            return Failure(ReservationCreationStatus.Unavailable, "This item is not available to reserve.");
        }

        var activeCount = await context.Reservations.CountAsync(reservation => reservation.UserId == userId
            && reservation.Status == ReservationStatus.Active && reservation.ExpiresAtUtc > countedAtUtc,
            cancellationToken);
        if (activeCount >= ReservationPolicy.MaximumActiveReservations)
        {
            return Failure(ReservationCreationStatus.LimitReached, "You already have three active reservations.");
        }

        var changed = await context.Listings
            .Where(listing => listing.Id == listingId && listing.Status == ListingStatus.Available)
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, ListingStatus.Held),
                cancellationToken);
        if (changed != 1)
        {
            return Conflict();
        }

        // Recheck immediately before saving, after all reads/updates. If the clock
        // went backward, retry rather than using an active count from a later time.
        var finalWindow = pickupHours.Evaluate();
        if (!finalWindow.IsEligible)
        {
            return PickupUnavailable(finalWindow.Status);
        }
        if (finalWindow.StartsAtUtc!.Value.UtcDateTime < countedAtUtc)
        {
            return Conflict();
        }

        var nowUtc = finalWindow.StartsAtUtc.Value.UtcDateTime;
        var reservation = new Reservation
        {
            ListingId = listingId,
            UserId = userId,
            Status = ReservationStatus.Active,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = finalWindow.ExpiresAtUtc!.Value.UtcDateTime
        };
        var audit = new AuditEvent
        {
            ListingId = listingId,
            ActorUserId = userId,
            Action = InventoryAuditActions.Reserved,
            OccurredAtUtc = nowUtc,
            Details = "Status changed from Available to Held by reservation creation."
        };
        context.Reservations.Add(reservation);
        context.AuditEvents.Add(audit);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Leave no failed additions that a later SaveChanges could accidentally retry.
            context.Entry(audit).State = EntityState.Detached;
            context.Entry(reservation).State = EntityState.Detached;
            throw;
        }

        return new(ReservationCreationStatus.Success, "The item is reserved for one hour.",
            reservation.Id, reservation.ExpiresAtUtc);
    }

    private async Task<bool?> LockUserAsync(string userId, bool postgres, CancellationToken cancellationToken)
    {
        if (postgres)
        {
            return await context.Users
                .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
                .AsNoTracking().Select(user => (bool?)user.IsActive).SingleOrDefaultAsync(cancellationToken);
        }

        // SQLite has no row-level FOR UPDATE. Its serializable write transaction
        // coordinates writers; this unchanged-value UPDATE takes its writer lock.
        await context.Users.Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.ConcurrencyStamp,
                user => user.ConcurrencyStamp), cancellationToken);
        return await context.Users.Where(user => user.Id == userId)
            .Select(user => (bool?)user.IsActive).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<ListingStatus?> LockListingAsync(int listingId, bool postgres, CancellationToken cancellationToken)
    {
        if (postgres)
        {
            return await context.Listings
                .FromSqlInterpolated($"SELECT * FROM \"Listings\" WHERE \"Id\" = {listingId} FOR UPDATE")
                .AsNoTracking().Select(listing => (ListingStatus?)listing.Status).SingleOrDefaultAsync(cancellationToken);
        }

        await context.Listings.Where(listing => listing.Id == listingId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, listing => listing.Status),
                cancellationToken);
        return await context.Listings.Where(listing => listing.Id == listingId)
            .Select(listing => (ListingStatus?)listing.Status).SingleOrDefaultAsync(cancellationToken);
    }

    private static bool IsDatabaseConflict(Exception exception)
    {
        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        return postgres is not null && (postgres.SqlState is PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable
            || postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && postgres.ConstraintName == "IX_Reservations_ActiveListing");
    }

    private static ReservationCreationResult Failure(ReservationCreationStatus status, string message)
        => new(status, message);

    private static ReservationCreationResult Conflict()
        => Failure(ReservationCreationStatus.Conflict, "The item or your reservations changed. Refresh and try again.");

    private static ReservationCreationResult PickupUnavailable(PickupHoursStatus status)
        => Failure(ReservationCreationStatus.PickupUnavailable,
            status switch
            {
                PickupHoursStatus.ScheduleUnavailable => "Reservations are unavailable because valid pickup hours have not been configured.",
                PickupHoursStatus.Closed => "Reservations are unavailable because pickup hours are closed.",
                _ => "Reservations are unavailable because a full hour must fit before the current pickup period ends."
            });
}
