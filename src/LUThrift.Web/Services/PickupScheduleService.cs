using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.ViewModels;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LUThrift.Web.Services;

public sealed class PickupScheduleService(
    ApplicationDbContext context, TimeProvider timeProvider, ILogger<PickupScheduleService> logger)
{
    // One query gives a consistent aggregate snapshot. Reads never initialize a row
    // or copy configuration; a missing schedule remains explicitly unconfigured.
    public async Task<PickupScheduleSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var schedule = await ReadScheduleAsync(cancellationToken);
        return schedule is null ? new(null, "America/Chicago", [], []) : Snapshot(schedule);
    }

    public Task<PickupScheduleOperationResult> SaveAsync(
        UpdatePickupScheduleInput? input, ClaimsPrincipal? actor, CancellationToken cancellationToken = default)
        => WriteAsync(input, actor, importOnly: false, cancellationToken);

    // Explicit operator action, never called by startup. It may only create the
    // first persisted schedule, including an intentionally empty configuration.
    public Task<PickupScheduleOperationResult> ImportConfiguredAsync(
        ReservationOptions? configured, ClaimsPrincipal? actor, CancellationToken cancellationToken = default)
        => WriteAsync(configured is null ? null : new UpdatePickupScheduleInput
        {
            CampusTimeZone = configured.CampusTimeZone,
            WeeklyPickupIntervals = configured.WeeklyPickupIntervals,
            ClosedDates = configured.ClosedDates
        }, actor, importOnly: true, cancellationToken);

    private async Task<PickupScheduleOperationResult> WriteAsync(
        UpdatePickupScheduleInput? input, ClaimsPrincipal? actor, bool importOnly, CancellationToken cancellationToken)
    {
        var actorId = CurrentAccountAccess.GetUserId(actor);
        if (actorId is null)
            return new(PickupScheduleOperationStatus.AuthenticationRequired, "Sign in to manage pickup hours.");
        try
        {
            if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
            if (input is null) return Invalid("Pickup schedule information is required.");
            var validation = PickupScheduleValidator.Validate(new ReservationOptions
            {
                CampusTimeZone = input.CampusTimeZone,
                WeeklyPickupIntervals = input.WeeklyPickupIntervals,
                ClosedDates = input.ClosedDates
            });
            if (!validation.IsValid) return Invalid(validation.Errors.First());
            if (context.Database.CurrentTransaction is not null) return Conflict();

            var proposed = new PickupSchedule
            {
                Id = PickupSchedule.SingletonId, Version = Guid.NewGuid(), CampusTimeZone = validation.TimeZone!.Id,
                WeeklyIntervals = validation.Intervals.Select(interval => new PickupScheduleInterval
                {
                    ScheduleId = PickupSchedule.SingletonId, Day = interval.Day,
                    OpensAt = interval.OpensAt, ClosesAt = interval.ClosesAt
                }).ToList(),
                ClosedDates = validation.ClosedDates.Select(date => new PickupScheduleClosedDate
                {
                    ScheduleId = PickupSchedule.SingletonId, Date = date
                }).ToList()
            };
            return await SaveInTransactionAsync(proposed, input.ExpectedVersion, actorId, importOnly, cancellationToken);
        }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        catch (Exception exception) when (IsDatabaseConflict(exception)) { return Conflict(); }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError("Saving a pickup schedule failed during persistence.");
            return new(PickupScheduleOperationStatus.PersistenceFailed,
                "Pickup hours could not be saved. Refresh before trying again.");
        }
    }

    private async Task<PickupScheduleOperationResult> SaveInTransactionAsync(
        PickupSchedule proposed, Guid? expectedVersion, string actorId, bool importOnly, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(
            context.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
        var current = await ReadScheduleAsync(cancellationToken);
        if (importOnly && current is not null) return Conflict();
        if (current?.Version != expectedVersion) return Conflict();
        if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
        if (current is not null && SameSchedule(current, proposed))
            return new(PickupScheduleOperationStatus.Unchanged, "Pickup hours are unchanged.", current.Version);

        DetachSchedules();
        AdministrativeAuditEvent? audit = null;
        var committed = false;
        try
        {
            if (current is null)
            {
                context.PickupSchedules.Add(proposed);
            }
            else
            {
                // Compare and swap the version in SQL before replacing child rows.
                // The root write lock serializes editors until this transaction commits.
                var access = CurrentAccountAccess.AdministratorAccounts(context, actorId);
                var changed = await context.PickupSchedules
                    .Where(schedule => schedule.Id == PickupSchedule.SingletonId
                        && schedule.Version == expectedVersion && access.Any())
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(schedule => schedule.Version, proposed.Version)
                        .SetProperty(schedule => schedule.CampusTimeZone, proposed.CampusTimeZone), cancellationToken);
                if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
                if (changed == 0) return Conflict();
                await context.PickupScheduleIntervals.Where(row => row.ScheduleId == PickupSchedule.SingletonId)
                    .ExecuteDeleteAsync(cancellationToken);
                await context.PickupScheduleClosedDates.Where(row => row.ScheduleId == PickupSchedule.SingletonId)
                    .ExecuteDeleteAsync(cancellationToken);
                context.PickupScheduleIntervals.AddRange(proposed.WeeklyIntervals);
                context.PickupScheduleClosedDates.AddRange(proposed.ClosedDates);
            }

            audit = new AdministrativeAuditEvent
            {
                ActorUserId = actorId, TargetScheduleId = PickupSchedule.SingletonId,
                Action = importOnly ? AdministrativeAuditActions.PickupScheduleImported : AdministrativeAuditActions.PickupScheduleSaved,
                OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime
            };
            context.AdministrativeAuditEvents.Add(audit);
            if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
            await context.SaveChangesAsync(cancellationToken);
            if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return new(PickupScheduleOperationStatus.Saved,
                proposed.WeeklyIntervals.Count == 0 ? "Empty pickup schedule saved."
                    : "Pickup schedule saved.", proposed.Version);
        }
        finally
        {
            DetachSchedules();
            if (!committed && audit is not null) context.Entry(audit).State = EntityState.Detached;
        }
    }

    private Task<PickupSchedule?> ReadScheduleAsync(CancellationToken cancellationToken)
        => context.PickupSchedules.AsNoTracking().AsSingleQuery()
            .Include(schedule => schedule.WeeklyIntervals).Include(schedule => schedule.ClosedDates)
            .SingleOrDefaultAsync(schedule => schedule.Id == PickupSchedule.SingletonId, cancellationToken);

    private Task<bool> IsAdministratorAsync(string actorId, CancellationToken cancellationToken)
        => CurrentAccountAccess.AdministratorAccounts(context, actorId).AnyAsync(cancellationToken);

    private static PickupScheduleSnapshot Snapshot(PickupSchedule schedule)
        => new(schedule.Version, schedule.CampusTimeZone,
            schedule.WeeklyIntervals.OrderBy(row => row.Day).ThenBy(row => row.OpensAt).Select(row => new WeeklyPickupInterval
            {
                Day = row.Day.ToString(), OpensAt = row.OpensAt.ToString("HH:mm", CultureInfo.InvariantCulture),
                ClosesAt = row.ClosesAt.ToString("HH:mm", CultureInfo.InvariantCulture)
            }).ToArray(),
            schedule.ClosedDates.OrderBy(row => row.Date).Select(row => row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray());

    private static bool SameSchedule(PickupSchedule first, PickupSchedule second)
        => first.CampusTimeZone == second.CampusTimeZone
            && first.WeeklyIntervals.OrderBy(row => row.Day).ThenBy(row => row.OpensAt)
                .Select(row => (row.Day, row.OpensAt, row.ClosesAt))
                .SequenceEqual(second.WeeklyIntervals.OrderBy(row => row.Day).ThenBy(row => row.OpensAt)
                    .Select(row => (row.Day, row.OpensAt, row.ClosesAt)))
            && first.ClosedDates.Select(row => row.Date).Order().SequenceEqual(second.ClosedDates.Select(row => row.Date).Order());

    private void DetachSchedules()
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(entry =>
                     entry.Entity is PickupSchedule or PickupScheduleInterval or PickupScheduleClosedDate).ToArray())
            entry.State = EntityState.Detached;
    }

    private static bool IsDatabaseConflict(Exception exception)
    {
        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        return postgres is not null && (postgres.SqlState is PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable
            || postgres.SqlState == PostgresErrorCodes.UniqueViolation && postgres.ConstraintName == "PK_PickupSchedules");
    }
    private static PickupScheduleOperationResult Invalid(string message)
        => new(PickupScheduleOperationStatus.InvalidInput, message);
    private static PickupScheduleOperationResult AdministratorRequired()
        => new(PickupScheduleOperationStatus.AdministratorRequired, "An active Administrator account is required.");
    private static PickupScheduleOperationResult Conflict()
        => new(PickupScheduleOperationStatus.Conflict, "Pickup hours changed. Reload the current schedule before saving or importing.");
}
