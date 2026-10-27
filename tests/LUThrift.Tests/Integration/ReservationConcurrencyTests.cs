using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LUThrift.Tests.Integration;

public class ReservationConcurrencyTests
{
    internal const string ConnectionEnvironmentVariable = "LUTHRIFT_RESERVATION_TEST_CONNECTION";
    private static readonly DateTimeOffset InitialNow = new(2026, 1, 5, 15, 0, 0, TimeSpan.Zero);

    [ReservationPostgresFact]
    public async Task TwoStudentsContendingForOneListing_CommitOnlyOneReservationAndAudit()
    {
        await using var test = await TestDatabase.CreateAsync();
        var firstStudent = await test.AddStudentAsync();
        var secondStudent = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync();
        var clock = new ControlledClock(InitialNow);
        await using var blocker = await test.HoldRowAsync(false, firstStudent, listingId);

        var results = await test.RunBlockedAsync(blocker, clock, null,
            new Request(firstStudent, listingId), new Request(secondStudent, listingId));

        var success = Assert.Single(results, result => result.Succeeded);
        Assert.Equal(ReservationCreationStatus.Unavailable, Assert.Single(results, result => !result.Succeeded).Status);
        await using var database = test.Context();
        var reservation = await database.Reservations.AsNoTracking().SingleAsync();
        Assert.Equal(success.ReservationId, reservation.Id);
        Assert.Equal(listingId, reservation.ListingId);
        Assert.Contains(reservation.UserId, new[] { firstStudent, secondStudent });
        Assert.Equal(InitialNow.UtcDateTime, reservation.CreatedAtUtc);
        Assert.Equal(InitialNow.AddHours(1).UtcDateTime, reservation.ExpiresAtUtc);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(ListingStatus.Held, await ListingStatusAsync(database, listingId));
        var audit = await database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(listingId, audit.ListingId);
        Assert.Equal(reservation.UserId, audit.ActorUserId);
        Assert.Equal(InventoryAuditActions.Reserved, audit.Action);
        Assert.Equal(reservation.CreatedAtUtc, audit.OccurredAtUtc);
    }

    [ReservationPostgresFact]
    public async Task OneStudentWithTwoActiveReservations_CompetingListingsCannotExceedThree()
    {
        await using var test = await TestDatabase.CreateAsync();
        var student = await test.AddStudentAsync();
        for (var index = 0; index < 2; index++)
        {
            var held = await test.AddListingAsync(ListingStatus.Held);
            await test.AddReservationAsync(student, held, InitialNow.AddMinutes(45));
        }
        var firstListing = await test.AddListingAsync();
        var secondListing = await test.AddListingAsync();
        await using var blocker = await test.HoldRowAsync(true, student, firstListing);

        var results = await test.RunBlockedAsync(blocker, new ControlledClock(InitialNow), null,
            new Request(student, firstListing), new Request(student, secondListing));

        Assert.Single(results, result => result.Succeeded);
        Assert.Equal(ReservationCreationStatus.LimitReached, Assert.Single(results, result => !result.Succeeded).Status);
        await using var database = test.Context();
        Assert.Equal(3, await database.Reservations.CountAsync(reservation => reservation.UserId == student
            && reservation.Status == ReservationStatus.Active && reservation.ExpiresAtUtc > InitialNow.UtcDateTime));
        var statuses = await database.Listings.Where(listing => listing.Id == firstListing || listing.Id == secondListing)
            .Select(listing => listing.Status).ToListAsync();
        Assert.Single(statuses, status => status == ListingStatus.Held);
        Assert.Single(statuses, status => status == ListingStatus.Available);
        var audit = await database.AuditEvents.SingleAsync();
        Assert.Equal(student, audit.ActorUserId);
        Assert.Equal(InventoryAuditActions.Reserved, audit.Action);
    }

    [ReservationPostgresFact]
    public async Task OwnDuplicateRequest_ReturnsAlreadyReservedWithoutExtendingOrDuplicatingTheReservation()
    {
        await using var test = await TestDatabase.CreateAsync();
        var student = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync();
        var clock = new ControlledClock(InitialNow);

        var first = await test.ReserveAsync(new(student, listingId), clock, "first");
        clock.AdvanceTo(InitialNow.AddMinutes(10));
        var duplicate = await test.ReserveAsync(new(student, listingId), clock, "duplicate");

        Assert.True(first.Succeeded);
        Assert.Equal(ReservationCreationStatus.AlreadyReserved, duplicate.Status);
        Assert.False(duplicate.Succeeded);
        await using var database = test.Context();
        var reservation = await database.Reservations.AsNoTracking().SingleAsync();
        Assert.Equal(first.ReservationId, reservation.Id);
        Assert.Equal(InitialNow.UtcDateTime, reservation.CreatedAtUtc);
        Assert.Equal(InitialNow.AddHours(1).UtcDateTime, reservation.ExpiresAtUtc);
        Assert.Equal(ListingStatus.Held, await ListingStatusAsync(database, listingId));
        Assert.Single(await database.AuditEvents.ToListAsync());
    }

    [ReservationPostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitingForUserOrListingLock_RechecksTheClockAndRejectsAnInsufficientPickupWindow(bool userLock)
    {
        await using var test = await TestDatabase.CreateAsync();
        var student = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync();
        var clock = new ControlledClock(InitialNow);
        await using var blocker = await test.HoldRowAsync(userLock, student, listingId);

        // At 11:00:01 campus time, a full hour no longer fits before the test closing time of noon.
        var results = await test.RunBlockedAsync(blocker, clock, InitialNow.AddHours(2).AddSeconds(1),
            new Request(student, listingId));

        var result = Assert.Single(results);
        Assert.Equal(ReservationCreationStatus.PickupUnavailable, result.Status);
        Assert.Null(result.ReservationId);
        Assert.Null(result.ExpiresAtUtc);
        await using var database = test.Context();
        Assert.Equal(ListingStatus.Available, await ListingStatusAsync(database, listingId));
        Assert.Empty(await database.Reservations.ToListAsync());
        Assert.Empty(await database.AuditEvents.ToListAsync());
    }

    [ReservationPostgresFact]
    public async Task DatabaseIndex_RejectsASecondStoredActiveRowEvenWhenTheFirstHasExpired()
    {
        await using var test = await TestDatabase.CreateAsync();
        var firstStudent = await test.AddStudentAsync();
        var secondStudent = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync(ListingStatus.Held);
        await test.AddReservationAsync(firstStudent, listingId, InitialNow.AddMinutes(-1));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => test.AddReservationAsync(secondStudent, listingId, InitialNow.AddHours(1)));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_Reservations_ActiveListing", postgres.ConstraintName);
        // The index protects stored Active history; it still permits non-active history for the same listing.
        await test.AddReservationAsync(secondStudent, listingId, InitialNow.AddMinutes(-2), ReservationStatus.Canceled);
        await using var database = test.Context();
        Assert.Equal(2, await database.Reservations.CountAsync());
        Assert.Equal(1, await database.Reservations.CountAsync(reservation => reservation.Status == ReservationStatus.Active));
    }

    [ReservationPostgresFact]
    public async Task AuditInsertFailure_RollsBackTheHoldAndReservationAndReturnsASafeFailure()
    {
        await using var test = await TestDatabase.CreateAsync();
        var student = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync();
        await using (var database = test.Context())
        {
            await database.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_reservation_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Reservation audit test failure'; END;
                $$;
                CREATE TRIGGER reject_reservation_audit BEFORE INSERT ON "AuditEvents"
                FOR EACH ROW EXECUTE FUNCTION reject_reservation_audit();
                """);
        }

        var result = await test.ReserveAsync(new(student, listingId), new ControlledClock(InitialNow), "audit_failure");

        Assert.Equal(ReservationCreationStatus.PersistenceFailed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.ReservationId);
        Assert.Null(result.ExpiresAtUtc);
        Assert.DoesNotContain("Reservation audit test failure", result.Message);
        Assert.DoesNotContain("Postgres", result.Message, StringComparison.OrdinalIgnoreCase);
        await using var check = test.Context();
        Assert.Equal(ListingStatus.Available, await ListingStatusAsync(check, listingId));
        Assert.Empty(await check.Reservations.ToListAsync());
        Assert.Empty(await check.AuditEvents.ToListAsync());
    }

    [ReservationPostgresFact]
    public async Task CancellationAndExpirationAtTheDeadline_CommitOneExpirationAndOneAudit()
    {
        await using var test = await TestDatabase.CreateAsync();
        var student = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync(ListingStatus.Held);
        var reservationId = await test.AddReservationAsync(student, listingId, InitialNow);
        var clock = new ControlledClock(InitialNow);
        await using var blocker = await test.HoldRowAsync(true, student, listingId);

        var results = await test.RunBlockedOperationsAsync<ReservationOperationResult>(blocker, clock, null, true,
            (name, token) => test.TransitionAsync(reservationId, student, clock, name, token),
            (name, token) => test.TransitionAsync(reservationId, null, clock, name, token));

        Assert.Equal(ReservationOperationStatus.Expired, Assert.Single(results, result => result.Changed).Status);
        Assert.Equal(ReservationOperationStatus.NotActive, Assert.Single(results, result => !result.Changed).Status);
        await using var database = test.Context();
        var reservation = await database.Reservations.SingleAsync();
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.Null(reservation.CancellationReason);
        Assert.Equal(ListingStatus.Available, await ListingStatusAsync(database, listingId));
        var audit = await database.AuditEvents.SingleAsync();
        Assert.Equal(InventoryAuditActions.ReservationExpired, audit.Action);
        Assert.Equal(ReservationService.ExpirationActorId, audit.ActorUserId);
        Assert.Equal(InitialNow.UtcDateTime, audit.OccurredAtUtc);
    }

    [ReservationPostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationWaitingForOwnerOrListing_UsesDeadlineAfterTheLockWait(bool userLock)
    {
        await using var test = await TestDatabase.CreateAsync();
        var student = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync(ListingStatus.Held);
        var deadline = InitialNow.AddMinutes(1);
        var reservationId = await test.AddReservationAsync(student, listingId, deadline);
        var clock = new ControlledClock(InitialNow);
        await using var blocker = await test.HoldRowAsync(userLock, student, listingId);

        var results = await test.RunBlockedOperationsAsync<ReservationOperationResult>(blocker, clock, deadline, true,
            (name, token) => test.TransitionAsync(reservationId, student, clock, name, token));

        Assert.Equal(ReservationOperationStatus.Expired, Assert.Single(results).Status);
        await using var database = test.Context();
        var reservation = await database.Reservations.SingleAsync();
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.Equal(deadline.UtcDateTime, reservation.ExpiresAtUtc);
        Assert.Null(reservation.CancellationReason);
        Assert.Equal(ListingStatus.Available, await ListingStatusAsync(database, listingId));
        var audit = await database.AuditEvents.SingleAsync();
        Assert.Equal(InventoryAuditActions.ReservationExpired, audit.Action);
        Assert.Equal(ReservationService.ExpirationActorId, audit.ActorUserId);
        Assert.Equal(deadline.UtcDateTime, audit.OccurredAtUtc);
    }

    [ReservationPostgresFact]
    public async Task DelayedAndRepeatedExpiration_PreservesANewerReservationAndItsHold()
    {
        await using var test = await TestDatabase.CreateAsync();
        var oldStudent = await test.AddStudentAsync();
        var newStudent = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync(ListingStatus.Held);
        var oldId = await test.AddReservationAsync(oldStudent, listingId, InitialNow);
        var clock = new ControlledClock(InitialNow);
        Assert.Equal(ReservationOperationStatus.Expired,
            (await test.TransitionAsync(oldId, null, clock, "first_expiration")).Status);
        var created = await test.ReserveAsync(new(newStudent, listingId), clock, "new_hold");
        Assert.True(created.Succeeded);
        await using var blocker = await test.HoldRowAsync(false, oldStudent, listingId);
        var delayedClock = new ControlledClock(InitialNow.AddMinutes(5));

        var results = await test.RunBlockedOperationsAsync<ReservationOperationResult>(blocker, delayedClock, null, true,
            (name, token) => test.TransitionAsync(oldId, null, delayedClock, name, token));
        Assert.Equal(ReservationOperationStatus.NotActive, Assert.Single(results).Status);
        Assert.Equal(ReservationOperationStatus.NotActive,
            (await test.TransitionAsync(oldId, null, delayedClock, "repeated_expiration")).Status);

        await using var database = test.Context();
        var newer = await database.Reservations.SingleAsync(row => row.Id == created.ReservationId);
        Assert.Equal(newStudent, newer.UserId);
        Assert.Equal(ReservationStatus.Active, newer.Status);
        Assert.Equal(InitialNow.UtcDateTime, newer.CreatedAtUtc);
        Assert.Equal(InitialNow.AddHours(1).UtcDateTime, newer.ExpiresAtUtc);
        Assert.Equal(ListingStatus.Held, await ListingStatusAsync(database, listingId));
        Assert.Equal(2, await database.Reservations.CountAsync());
        Assert.Equal(2, await database.AuditEvents.CountAsync());
    }

    [ReservationPostgresFact]
    public async Task CompetingStudents_ReconcileAnotherOwnersExpiredHoldBeforeCreatingOneNewReservation()
    {
        await using var test = await TestDatabase.CreateAsync();
        var oldStudent = await test.AddStudentAsync();
        var firstStudent = await test.AddStudentAsync();
        var secondStudent = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync(ListingStatus.Held);
        var oldId = await test.AddReservationAsync(oldStudent, listingId, InitialNow);
        var clock = new ControlledClock(InitialNow);
        await using var blocker = await test.HoldRowAsync(true, oldStudent, listingId);

        // Reconciliation may inspect the deadline before locking; its transition
        // and the later creation must each recheck under their own owner→listing locks.
        var results = await test.RunBlockedOperationsAsync<ReservationCreationResult>(blocker, clock, null, false,
            (name, token) => test.ReserveAsync(new(firstStudent, listingId), clock, name, token),
            (name, token) => test.ReserveAsync(new(secondStudent, listingId), clock, name, token));

        var success = Assert.Single(results, result => result.Succeeded);
        Assert.Equal(ReservationCreationStatus.Unavailable, Assert.Single(results, result => !result.Succeeded).Status);
        await using var database = test.Context();
        Assert.Equal(ReservationStatus.Expired,
            await database.Reservations.Where(row => row.Id == oldId).Select(row => row.Status).SingleAsync());
        var current = await database.Reservations.SingleAsync(row => row.Status == ReservationStatus.Active);
        Assert.Equal(success.ReservationId, current.Id);
        Assert.Contains(current.UserId, new[] { firstStudent, secondStudent });
        Assert.Equal(InitialNow.AddHours(1).UtcDateTime, current.ExpiresAtUtc);
        Assert.Equal(ListingStatus.Held, await ListingStatusAsync(database, listingId));
        Assert.Equal(2, await database.Reservations.CountAsync());
        var audits = await database.AuditEvents.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(InventoryAuditActions.ReservationExpired, audits[0].Action);
        Assert.Equal(ReservationService.ExpirationActorId, audits[0].ActorUserId);
        Assert.Equal(InventoryAuditActions.Reserved, audits[1].Action);
        Assert.Equal(current.UserId, audits[1].ActorUserId);
    }

    [ReservationPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransitionAuditFailure_RollsBackReservationAndHoldAndCanBeRetried(bool expiration)
    {
        await using var test = await TestDatabase.CreateAsync();
        var student = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync(ListingStatus.Held);
        var deadline = expiration ? InitialNow : InitialNow.AddHours(1);
        var reservationId = await test.AddReservationAsync(student, listingId, deadline);
        await using var database = test.Context();
        var before = ReservationSnapshot(await database.Reservations.AsNoTracking().SingleAsync());
        await database.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_transition_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Transition audit test failure'; END;
            $$;
            CREATE TRIGGER reject_transition_audit BEFORE INSERT ON "AuditEvents"
            FOR EACH ROW EXECUTE FUNCTION reject_transition_audit();
            """);

        var result = await test.TransitionAsync(reservationId, expiration ? null : student,
            new ControlledClock(InitialNow), "transition_failure");

        Assert.Equal(ReservationOperationStatus.PersistenceFailed, result.Status);
        Assert.False(result.Changed);
        Assert.DoesNotContain("Transition audit test failure", result.Message);
        Assert.DoesNotContain("Postgres", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, ReservationSnapshot(await database.Reservations.AsNoTracking().SingleAsync()));
        Assert.Equal(ListingStatus.Held, await ListingStatusAsync(database, listingId));
        Assert.Empty(await database.AuditEvents.ToListAsync());

        await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_transition_audit ON \"AuditEvents\";");
        var retry = await test.TransitionAsync(reservationId, expiration ? null : student,
            new ControlledClock(InitialNow), "transition_retry");
        Assert.Equal(expiration ? ReservationOperationStatus.Expired : ReservationOperationStatus.Canceled, retry.Status);
        Assert.Equal(ListingStatus.Available, await ListingStatusAsync(database, listingId));
        Assert.Single(await database.AuditEvents.ToListAsync());
    }

    [ReservationPostgresFact]
    public async Task Migration_PreflightRefusesDuplicateActiveRowsWithoutChangingExistingData()
    {
        await using var test = await TestDatabase.CreateAsync(previousMigration: true);
        var firstStudent = await test.AddStudentAsync();
        var secondStudent = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync(ListingStatus.Held);
        await test.AddReservationAsync(firstStudent, listingId, InitialNow.AddHours(1));
        await test.AddReservationAsync(secondStudent, listingId, InitialNow.AddMinutes(-1));
        await using var database = test.Context();
        var before = await database.Reservations.AsNoTracking().OrderBy(reservation => reservation.Id).ToListAsync();
        var appliedBefore = (await database.Database.GetAppliedMigrationsAsync()).ToArray();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.Database.MigrateAsync());

        Assert.Equal("P0001", exception.SqlState);
        Assert.StartsWith("Cannot enforce one Active reservation per listing. Conflicting listing IDs: ", exception.MessageText);
        Assert.Contains(listingId.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.MessageText);
        Assert.Equal(appliedBefore, await database.Database.GetAppliedMigrationsAsync());
        var after = await database.Reservations.AsNoTracking().OrderBy(reservation => reservation.Id).ToListAsync();
        Assert.Equal(before.Select(ReservationSnapshot), after.Select(ReservationSnapshot));
        Assert.Equal(ListingStatus.Held, await ListingStatusAsync(database, listingId));
        Assert.Empty(await database.AuditEvents.ToListAsync());
    }

    [ReservationPostgresFact]
    public async Task AccountOverview_FiltersOwnerAndBoundsHistoryInPostgresWithoutCleanupWrites()
    {
        await using var test = await TestDatabase.CreateAsync();
        var owner = await test.AddStudentAsync();
        var other = await test.AddStudentAsync();
        var currentIds = new List<int>();
        for (var index = 0; index < 3; index++)
        {
            currentIds.Add(await test.AddReservationAsync(owner,
                await test.AddListingAsync(ListingStatus.Held), InitialNow.AddMinutes(index + 10)));
        }
        var historyIds = new List<int>();
        for (var index = 0; index < ReservationOverview.RecentHistoryLimit + 3; index++)
        {
            historyIds.Add(await test.AddReservationAsync(owner,
                await test.AddListingAsync(ListingStatus.Archived), InitialNow.AddMinutes(-1), ReservationStatus.Canceled));
        }
        var expiredListing = await test.AddListingAsync(ListingStatus.Held);
        var expiredId = await test.AddReservationAsync(owner, expiredListing, InitialNow);
        historyIds.Add(expiredId);
        // Higher IDs with identical timestamps must not consume the owner's history limit.
        for (var index = 0; index < 3; index++)
        {
            await test.AddReservationAsync(other, await test.AddListingAsync(ListingStatus.Collected),
                InitialNow.AddMinutes(-1), ReservationStatus.Fulfilled);
        }
        await using var database = test.Context();
        var before = await database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        var clock = new ControlledClock(InitialNow);

        var overview = await test.OverviewAsync(owner, clock);

        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(3, overview.ActiveCount);
        Assert.Equal(currentIds.OrderDescending(), overview.Current.Select(row => row.Id));
        Assert.Equal(historyIds.OrderDescending().Take(ReservationOverview.RecentHistoryLimit),
            overview.RecentHistory.Select(row => row.Id));
        Assert.All(overview.Current, row => Assert.True(row.CanCancel));
        Assert.All(overview.RecentHistory, row => Assert.False(row.CanCancel));
        Assert.Equal(ReservationStatus.Expired, overview.RecentHistory.Single(row => row.Id == expiredId).Status);
        var otherOverview = await test.OverviewAsync(other, new ControlledClock(InitialNow));
        Assert.Empty(otherOverview.Current);
        Assert.Equal(3, otherOverview.RecentHistory.Count);
        Assert.DoesNotContain(otherOverview.RecentHistory, row => historyIds.Contains(row.Id));
        var after = await database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(before.Select(ReservationSnapshot), after.Select(ReservationSnapshot));
        Assert.Equal(ReservationStatus.Active, after.Single(row => row.Id == expiredId).Status);
        Assert.Equal(ListingStatus.Held, await ListingStatusAsync(database, expiredListing));
        Assert.Empty(await database.AuditEvents.ToListAsync());
    }

    private static object ReservationSnapshot(Reservation reservation)
        => (reservation.Id, reservation.ListingId, reservation.UserId, reservation.Status,
            reservation.CreatedAtUtc, reservation.ExpiresAtUtc, reservation.FulfilledAtUtc, reservation.CancellationReason);

    private static Task<ListingStatus> ListingStatusAsync(ApplicationDbContext database, int listingId)
        => database.Listings.Where(listing => listing.Id == listingId).Select(listing => listing.Status).SingleAsync();

    private sealed record Request(string UserId, int ListingId);

    private sealed class ControlledClock(DateTimeOffset now) : TimeProvider
    {
        private long utcTicks = now.UtcTicks;
        private int reads;
        public int ReadCount => Volatile.Read(ref reads);
        public void AdvanceTo(DateTimeOffset instant) => Interlocked.Exchange(ref utcTicks, instant.UtcTicks);
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref reads);
            return new DateTimeOffset(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
        }
    }

    private sealed class TestDatabase(string baseConnection, string schema) : IAsyncDisposable
    {
        private const string StudentRoleId = "reservation-test-student";
        private int nextListingId = 1000;

        public static async Task<TestDatabase> CreateAsync(bool previousMigration = false)
        {
            var baseConnection = GuardedConnection();
            var schema = "reservation_test_" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(baseConnection))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
                await create.ExecuteNonQueryAsync();
            }
            var test = new TestDatabase(baseConnection, schema);
            try
            {
                await using var database = test.Context("migrate");
                await database.GetService<IMigrator>().MigrateAsync(
                    previousMigration ? "20261004204501_AddInventoryAuditEvents" : null);
                database.Roles.Add(new IdentityRole
                {
                    Id = StudentRoleId, Name = ApplicationRoles.Student,
                    NormalizedName = ApplicationRoles.Student.ToUpperInvariant(), ConcurrencyStamp = Guid.NewGuid().ToString()
                });
                await database.SaveChangesAsync();
                return test;
            }
            catch
            {
                await test.DisposeAsync();
                throw;
            }
        }

        public ApplicationDbContext Context(string name = "verify")
            => new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(Connection(name), options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);

        private string Connection(string name)
            => new NpgsqlConnectionStringBuilder(baseConnection)
            {
                SearchPath = schema, ApplicationName = ApplicationName(name), Pooling = false,
                Timeout = 5, CommandTimeout = 20
            }.ConnectionString;

        private string ApplicationName(string name) => schema + "_" + name;

        public async Task<string> AddStudentAsync()
        {
            var id = Guid.NewGuid().ToString();
            await using var database = Context();
            database.Users.Add(new ApplicationUser
            {
                Id = id, DisplayName = "Reservation test student", UserName = "student-" + id,
                IsActive = true, CreatedAtUtc = InitialNow.UtcDateTime
            });
            database.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = StudentRoleId });
            await database.SaveChangesAsync();
            return id;
        }

        public async Task<int> AddListingAsync(ListingStatus status = ListingStatus.Available)
        {
            var id = Interlocked.Increment(ref nextListingId);
            await using var database = Context();
            database.Listings.Add(new Listing
            {
                Id = id, Title = "Reservation test item", Description = "An isolated concurrency-test listing.",
                Condition = "Good", CategoryId = 1, Status = status, CreatedAtUtc = InitialNow.UtcDateTime
            });
            await database.SaveChangesAsync();
            return id;
        }

        public async Task<int> AddReservationAsync(string userId, int listingId, DateTimeOffset expires,
            ReservationStatus status = ReservationStatus.Active)
        {
            await using var database = Context();
            var reservation = new Reservation
            {
                UserId = userId, ListingId = listingId, Status = status,
                CreatedAtUtc = InitialNow.AddHours(-2).UtcDateTime, ExpiresAtUtc = expires.UtcDateTime
            };
            database.Reservations.Add(reservation);
            await database.SaveChangesAsync();
            return reservation.Id;
        }

        public async Task<ReservationCreationResult> ReserveAsync(Request request, ControlledClock clock, string name,
            CancellationToken cancellationToken = default)
        {
            await using var database = Context(name);
            return await Service(database, clock).CreateAsync(new CreateReservationInput { ListingId = request.ListingId },
                StudentPrincipal(request.UserId), cancellationToken);
        }

        public async Task<ReservationOperationResult> TransitionAsync(int reservationId, string? cancelingUserId,
            ControlledClock clock, string name, CancellationToken cancellationToken = default)
        {
            await using var database = Context(name);
            var service = Service(database, clock);
            return cancelingUserId is null
                ? await service.ExpireAsync(reservationId, cancellationToken)
                : await service.CancelAsync(reservationId, StudentPrincipal(cancelingUserId), cancellationToken);
        }

        public async Task<ReservationOverview> OverviewAsync(string userId, ControlledClock clock)
        {
            await using var database = Context("overview");
            var overview = await Service(database, clock).GetForUserAsync(StudentPrincipal(userId));
            Assert.Empty(database.ChangeTracker.Entries());
            return overview;
        }

        private static ReservationService Service(ApplicationDbContext database, ControlledClock clock)
        {
            // These hours are a controlled test fixture, not an official pickup schedule.
            var hours = new PickupHoursEligibility(Options.Create(new ReservationOptions
            {
                CampusTimeZone = "America/Chicago",
                WeeklyPickupIntervals = [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "12:00" }]
            }), clock);
            return new ReservationService(database, hours, NullLogger<ReservationService>.Instance, clock);
        }

        private static ClaimsPrincipal StudentPrincipal(string userId)
            => new(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, ApplicationRoles.Student)],
                "Reservation integration test"));

        public async Task<RowLock> HoldRowAsync(bool userRow, string userId, int listingId)
        {
            var connection = new NpgsqlConnection(Connection("blocker"));
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            try
            {
                var sql = userRow
                    ? "SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Id\" = @id FOR UPDATE"
                    : "SELECT \"Id\" FROM \"Listings\" WHERE \"Id\" = @id FOR UPDATE";
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("id", userRow ? userId : (object)listingId);
                Assert.NotNull(await command.ExecuteScalarAsync());
                return new RowLock(connection, transaction);
            }
            catch
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<ReservationCreationResult[]> RunBlockedAsync(RowLock blocker, ControlledClock clock,
            DateTimeOffset? advanceTo, params Request[] requests)
            => RunBlockedOperationsAsync(blocker, clock, advanceTo, true, requests.Select(request =>
                new Func<string, CancellationToken, Task<ReservationCreationResult>>(
                    (name, token) => ReserveAsync(request, clock, name, token))).ToArray());

        public async Task<T[]> RunBlockedOperationsAsync<T>(RowLock blocker, ControlledClock clock,
            DateTimeOffset? advanceTo, bool assertClockNotRead,
            params Func<string, CancellationToken, Task<T>>[] operations)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var names = operations.Select((_, index) => "request" + index).ToArray();
            var tasks = operations.Select((operation, index) => operation(names[index], timeout.Token)).ToArray();
            try
            {
                await WaitUntilBlockedAsync(names.Select(ApplicationName).ToArray(), timeout.Token);
                if (assertClockNotRead)
                {
                    Assert.Equal(0, clock.ReadCount); // Final eligibility/deadline checks must follow both locks.
                }
                if (advanceTo is { } instant)
                {
                    clock.AdvanceTo(instant);
                }
            }
            catch
            {
                timeout.Cancel();
                throw;
            }
            finally
            {
                await blocker.ReleaseAsync();
                try
                {
                    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch when (timeout.IsCancellationRequested)
                {
                    // Preserve the lock-observation failure after canceling and releasing waiting requests.
                }
            }
            return await Task.WhenAll(tasks);
        }

        private async Task WaitUntilBlockedAsync(string[] applicationNames, CancellationToken cancellationToken)
        {
            await using var observer = new NpgsqlConnection(Connection("observer"));
            await observer.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT count(*)::integer FROM pg_stat_activity
                WHERE application_name = ANY(@names) AND wait_event_type = 'Lock'
                  AND cardinality(pg_blocking_pids(pid)) > 0
                """, observer);
            command.Parameters.AddWithValue("names", applicationNames);
            while ((int)(await command.ExecuteScalarAsync(cancellationToken))! != applicationNames.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Only this test's newly created, random schema is removed; no existing schema or database is reset.
            await using var connection = new NpgsqlConnection(baseConnection);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }

        private static string GuardedConnection()
        {
            NpgsqlConnectionStringBuilder connection;
            try
            {
                connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable));
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("The reservation PostgreSQL test connection is invalid.");
            }
            if (connection.Host is not ("localhost" or "127.0.0.1" or "::1")
                || connection.Database != "luthrift_reservation_test")
            {
                throw new InvalidOperationException(
                    "Reservation PostgreSQL tests require a loopback host and the dedicated luthrift_reservation_test database.");
            }
            connection.Pooling = false;
            return connection.ConnectionString;
        }
    }

    private sealed class RowLock(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private bool released;
        public async Task ReleaseAsync()
        {
            if (!released)
            {
                await transaction.RollbackAsync();
                released = true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

public sealed class ReservationPostgresFactAttribute : FactAttribute
{
    public ReservationPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReservationConcurrencyTests.ConnectionEnvironmentVariable)))
        {
            Skip = "Set LUTHRIFT_RESERVATION_TEST_CONNECTION to run against a disposable PostgreSQL test database.";
        }
    }
}

public sealed class ReservationPostgresTheoryAttribute : TheoryAttribute
{
    public ReservationPostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReservationConcurrencyTests.ConnectionEnvironmentVariable)))
        {
            Skip = "Set LUTHRIFT_RESERVATION_TEST_CONNECTION to run against a disposable PostgreSQL test database.";
        }
    }
}
