using System.Security.Claims;
using System.Text.Json;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class ReservationPickupTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task ConfirmPickup_UsesCurrentDatabaseRoleAndAtomicallyRecordsStaffFulfillment(string role)
    {
        await using var test = await Fixture.CreateAsync(role: role);
        var reservation = await test.SeedAsync();
        var createdAt = reservation.CreatedAtUtc;
        var expiresAt = reservation.ExpiresAtUtc;
        Assert.Empty(test.Actor.FindAll(ClaimTypes.Role));

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.Fulfilled, result.Status);
        Assert.True(result.Changed);
        var saved = await test.ReservationAsync(reservation.Id);
        Assert.Equal(ReservationStatus.Fulfilled, saved.Status);
        Assert.Equal(test.Clock.Now.UtcDateTime, saved.FulfilledAtUtc);
        Assert.Equal(Fixture.Student, saved.UserId);
        Assert.Equal(createdAt, saved.CreatedAtUtc);
        Assert.Equal(expiresAt, saved.ExpiresAtUtc);
        Assert.Null(saved.CancellationReason);
        Assert.Equal(ListingStatus.Collected, await test.ListingStatusAsync());
        var audit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(InventoryAuditActions.ReservationFulfilled, audit.Action);
        Assert.Equal(Fixture.Staff, audit.ActorUserId);
        Assert.NotEqual(saved.UserId, audit.ActorUserId);
        Assert.Equal(saved.ListingId, audit.ListingId);
        Assert.Equal(saved.FulfilledAtUtc, audit.OccurredAtUtc);
        Assert.Contains($"Reservation {saved.Id}", audit.Details);
        Assert.Contains("Active", audit.Details);
        Assert.Contains("Fulfilled", audit.Details);
        Assert.Contains("Held", audit.Details);
        Assert.Contains("Collected", audit.Details);
        Assert.DoesNotContain("@", audit.Details);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task ConfirmPickup_StrictDeadlineNeverCreatesALatePickup(long ticks, bool succeeds)
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddTicks(ticks));
        var reservation = await test.SeedAsync();
        var before = await test.SnapshotAsync();

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(succeeds ? ReservationOperationStatus.Fulfilled : ReservationOperationStatus.DeadlinePassed, result.Status);
        Assert.Equal(succeeds, result.Changed);
        if (!succeeds)
        {
            Assert.Equal(before, await test.SnapshotAsync());
            Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(reservation.Id)).Status);
            Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
            Assert.Empty(await test.Database.AuditEvents.ToListAsync());
        }
    }

    [Fact]
    public async Task ConfirmPickup_RechecksDeadlineImmediatelyBeforeTransition()
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddMinutes(-1));
        var reservation = await test.SeedAsync();
        var before = await test.SnapshotAsync();
        test.Clock.SetReadSequence(Deadline.AddTicks(-1), Deadline);

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.DeadlinePassed, result.Status);
        Assert.False(result.Changed);
        Assert.True(test.Clock.ReadCount >= 2);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task ConfirmPickup_SavesTheFinalServerTimeInsteadOfTheEarlierEligibilityTime()
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        var finalTime = Deadline.AddSeconds(-1);
        test.Clock.SetReadSequence(Deadline.AddMinutes(-5), finalTime);

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.Fulfilled, result.Status);
        Assert.Equal(finalTime.UtcDateTime, (await test.ReservationAsync(reservation.Id)).FulfilledAtUtc);
        Assert.Equal(finalTime.UtcDateTime, (await test.Database.AuditEvents.AsNoTracking().SingleAsync()).OccurredAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmPickup_DoesNotApplyCreationScheduleOrFullHourRequirement(bool configured)
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddSeconds(-10), configured: configured);
        var reservation = await test.SeedAsync();
        Assert.Equal(configured ? PickupHoursStatus.InsufficientTime : PickupHoursStatus.ScheduleUnavailable,
            test.Hours.Evaluate().Status);

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.Fulfilled, result.Status);
        Assert.Equal(Deadline.AddSeconds(-10).UtcDateTime, (await test.ReservationAsync(reservation.Id)).FulfilledAtUtc);
        Assert.Equal(ListingStatus.Collected, await test.ListingStatusAsync());
    }

    [Theory]
    [InlineData("null", ReservationOperationStatus.AuthenticationRequired)]
    [InlineData("anonymous", ReservationOperationStatus.AuthenticationRequired)]
    [InlineData("missing identifier", ReservationOperationStatus.AuthenticationRequired)]
    [InlineData("blank identifier", ReservationOperationStatus.AuthenticationRequired)]
    [InlineData("long identifier", ReservationOperationStatus.AuthenticationRequired)]
    [InlineData("unknown account", ReservationOperationStatus.StaffRequired)]
    [InlineData("inactive", ReservationOperationStatus.StaffRequired)]
    [InlineData("revoked", ReservationOperationStatus.StaffRequired)]
    [InlineData("student with forged staff claim", ReservationOperationStatus.StaffRequired)]
    public async Task ConfirmPickup_RequiresTrustedAuthenticationAndCurrentActiveStaffMembership(
        string scenario, ReservationOperationStatus expected)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        if (scenario == "inactive")
            await test.Database.Users.Where(row => row.Id == Fixture.Staff)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        if (scenario == "revoked")
            await test.Database.UserRoles.Where(row => row.UserId == Fixture.Staff).ExecuteDeleteAsync();
        var principal = scenario switch
        {
            "null" => null,
            "anonymous" => new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Fixture.Staff)])),
            "missing identifier" => new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, ApplicationRoles.Staff)], "Test")),
            "blank identifier" => Principal(" "),
            "long identifier" => Principal(new string('x', 451)),
            "unknown account" => Principal("unknown-account"),
            "student with forged staff claim" => new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Fixture.Student), new Claim(ClaimTypes.Role, ApplicationRoles.Staff)], "Test")),
            _ => test.Actor
        };
        var before = await test.SnapshotAsync();

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, principal);
        var unknown = await test.Service.ConfirmPickupAsync(int.MaxValue, principal);

        Assert.Equal(expected, result.Status);
        Assert.Equal(result, unknown);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Equal(0, test.Clock.ReadCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task ConfirmPickup_UnknownOrInvalidIdReturnsNotFoundWithoutWrites(int id)
    {
        await using var test = await Fixture.CreateAsync();
        await test.SeedAsync();
        var before = await test.SnapshotAsync();

        var result = await test.Service.ConfirmPickupAsync(id, test.Actor);

        Assert.Equal(ReservationOperationStatus.NotFound, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ReservationStatus.Canceled)]
    [InlineData(ReservationStatus.Expired)]
    [InlineData(ReservationStatus.Fulfilled)]
    public async Task ConfirmPickup_StoredTerminalReservationCannotBeCollectedAgain(ReservationStatus status)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync(status: status);
        var before = await test.SnapshotAsync();

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.NotActive, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Available)]
    [InlineData(ListingStatus.Missing)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Archived)]
    public async Task ConfirmPickup_RequiresCurrentHeldInventory(ListingStatus status)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync(listingStatus: status);
        var before = await test.SnapshotAsync();

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.InvalidState, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmPickup_RejectsActiveRowsWithConflictingHistoryMetadata(bool fulfilledTimestamp)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        if (fulfilledTimestamp)
            await test.Database.Reservations.Where(row => row.Id == reservation.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.FulfilledAtUtc, Deadline.AddMinutes(-20).UtcDateTime));
        else
            await test.Database.Reservations.Where(row => row.Id == reservation.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CancellationReason, "Historical cancellation."));
        var before = await test.SnapshotAsync();

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.InvalidState, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("newer history")]
    [InlineData("same timestamp higher ID")]
    [InlineData("other active hold")]
    public async Task ConfirmPickup_NeverCollectsAnItemForASupersededReservation(string scenario)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        if (scenario == "other active hold")
            await test.Database.Database.ExecuteSqlRawAsync("DROP INDEX IX_Reservations_ActiveListing;");
        var replacement = new Reservation
        {
            UserId = Fixture.OtherStudent, ListingId = reservation.ListingId,
            Status = scenario == "other active hold" ? ReservationStatus.Active : ReservationStatus.Canceled,
            CreatedAtUtc = scenario switch
            {
                "newer history" => reservation.CreatedAtUtc.AddMinutes(1),
                "same timestamp higher ID" => reservation.CreatedAtUtc,
                _ => reservation.CreatedAtUtc.AddMinutes(-1)
            },
            ExpiresAtUtc = Deadline.AddMinutes(5).UtcDateTime
        };
        test.Database.Reservations.Add(replacement);
        await test.Database.SaveChangesAsync();
        test.Database.ChangeTracker.Clear();
        var before = await test.SnapshotAsync();

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.InvalidState, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
    }

    [Fact]
    public async Task ConfirmPickup_OlderHistoryDoesNotPreventCollectingTheCurrentHold()
    {
        await using var test = await Fixture.CreateAsync();
        var current = await test.SeedAsync();
        var older = new Reservation
        {
            UserId = Fixture.OtherStudent, ListingId = current.ListingId, Status = ReservationStatus.Canceled,
            CreatedAtUtc = current.CreatedAtUtc.AddHours(-2), ExpiresAtUtc = current.CreatedAtUtc.AddHours(-1),
            CancellationReason = "Canceled by student."
        };
        test.Database.Reservations.Add(older);
        await test.Database.SaveChangesAsync();
        test.Database.ChangeTracker.Clear();

        var result = await test.Service.ConfirmPickupAsync(current.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.Fulfilled, result.Status);
        Assert.Equal(ListingStatus.Collected, await test.ListingStatusAsync());
        var history = await test.ReservationAsync(older.Id);
        Assert.Equal(ReservationStatus.Canceled, history.Status);
        Assert.Equal(older.CreatedAtUtc, history.CreatedAtUtc);
        Assert.Equal(older.ExpiresAtUtc, history.ExpiresAtUtc);
        Assert.Equal(older.CancellationReason, history.CancellationReason);
        Assert.Single(await test.Database.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task ConfirmPickup_RepeatedProcessingPreservesOriginalTimeAuditAndCollectedItem()
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        Assert.True((await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor)).Changed);
        var first = await test.ReservationAsync(reservation.Id);
        var before = await test.SnapshotAsync();
        test.Clock.Now = Deadline.AddHours(1);

        var repeated = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);
        var expired = await test.Service.ExpireAsync(reservation.Id);
        var canceled = await test.Service.CancelAsync(reservation.Id, Principal(Fixture.Student));

        Assert.Equal(ReservationOperationStatus.NotActive, repeated.Status);
        Assert.False(repeated.Changed);
        Assert.False(expired.Changed);
        Assert.False(canceled.Changed);
        Assert.Equal(first.FulfilledAtUtc, (await test.ReservationAsync(reservation.Id)).FulfilledAtUtc);
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Single(await test.Database.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("reservation")]
    [InlineData("listing")]
    [InlineData("audit")]
    public async Task ConfirmPickup_FailedWritesRollbackAllChangesAndLeaveNoDeferredAudit(string failure)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        var trigger = failure switch
        {
            "reservation" => "CREATE TRIGGER fail_pickup BEFORE UPDATE ON Reservations WHEN NEW.Status = 'Fulfilled' BEGIN SELECT RAISE(ABORT, 'private database failure'); END;",
            "listing" => "CREATE TRIGGER fail_pickup BEFORE UPDATE OF Status ON Listings WHEN NEW.Status = 'Collected' BEGIN SELECT RAISE(ABORT, 'private database failure'); END;",
            _ => "CREATE TRIGGER fail_pickup BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'private database failure'); END;"
        };
        await test.Database.Database.ExecuteSqlRawAsync(trigger);
        var before = await test.SnapshotAsync();

        var result = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.PersistenceFailed, result.Status);
        Assert.False(result.Changed);
        Assert.DoesNotContain("private database failure", result.Message);
        Assert.All(test.Logger.Entries, entry => Assert.DoesNotContain("private database failure", entry.Message));
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Null((await test.ReservationAsync(reservation.Id)).FulfilledAtUtc);
        await test.Database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_pickup;");
        await test.Database.SaveChangesAsync();
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Equal(ReservationOperationStatus.Fulfilled, (await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor)).Status);
        Assert.Single(await test.Database.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task ConfirmPickup_CancellationPropagatesWithoutChangingRecords()
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        var before = await test.SnapshotAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => test.Service.ConfirmPickupAsync(reservation.Id, test.Actor, cancellation.Token));

        Assert.Equal(before, await test.SnapshotAsync());
    }

    private static ClaimsPrincipal Principal(string id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Test"));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> readings = new();
        public DateTimeOffset Now { get; set; } = now;
        public int ReadCount { get; private set; }
        public void SetReadSequence(params DateTimeOffset[] values)
        {
            readings.Clear();
            foreach (var value in values) readings.Enqueue(value);
        }
        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            if (readings.TryDequeue(out var next)) Now = next;
            return Now;
        }
    }

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext database,
        ReservationService service, PickupHoursEligibility hours, Clock clock, RecordingLogger<ReservationService> logger) : IAsyncDisposable
    {
        public const string Staff = "pickup-staff";
        public const string Student = "pickup-owner";
        public const string OtherStudent = "pickup-other-student";
        public ApplicationDbContext Database { get; } = database;
        public ReservationService Service { get; } = service;
        public PickupHoursEligibility Hours { get; } = hours;
        public Clock Clock { get; } = clock;
        public RecordingLogger<ReservationService> Logger { get; } = logger;
        public ClaimsPrincipal Actor { get; } = Principal(Staff);

        public static async Task<Fixture> CreateAsync(DateTimeOffset? now = null,
            string role = ApplicationRoles.Staff, bool configured = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var database = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await database.Database.EnsureCreatedAsync();
            database.Users.AddRange(
                new ApplicationUser { Id = Staff, DisplayName = "Pickup staff" },
                new ApplicationUser { Id = Student, DisplayName = "Pickup student" },
                new ApplicationUser { Id = OtherStudent, DisplayName = "Other pickup student" });
            database.Roles.AddRange(new IdentityRole { Id = "staff-role", Name = role },
                new IdentityRole { Id = "student-role", Name = ApplicationRoles.Student });
            database.UserRoles.AddRange(new IdentityUserRole<string> { UserId = Staff, RoleId = "staff-role" },
                new IdentityUserRole<string> { UserId = Student, RoleId = "student-role" },
                new IdentityUserRole<string> { UserId = OtherStudent, RoleId = "student-role" });
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            var clock = new Clock(now ?? Deadline.AddMinutes(-10));
            var hours = new PickupHoursEligibility(Options.Create(new ReservationOptions
            {
                CampusTimeZone = "UTC",
                // Synthetic schedule; official pickup hours remain unconfirmed.
                WeeklyPickupIntervals = configured ? [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "17:00" }] : []
            }), clock);
            var logger = new RecordingLogger<ReservationService>();
            return new(connection, database, new ReservationService(database, hours, logger, clock), hours, clock, logger);
        }

        public async Task<Reservation> SeedAsync(ReservationStatus status = ReservationStatus.Active,
            ListingStatus listingStatus = ListingStatus.Held)
        {
            var reservation = new Reservation
            {
                ListingId = 1, UserId = Student, Status = status,
                CreatedAtUtc = Deadline.AddHours(-1).UtcDateTime, ExpiresAtUtc = Deadline.UtcDateTime,
                FulfilledAtUtc = status == ReservationStatus.Fulfilled ? Deadline.AddMinutes(-20).UtcDateTime : null,
                CancellationReason = status == ReservationStatus.Canceled ? "Canceled by student." : null
            };
            Database.Reservations.Add(reservation);
            await Database.Listings.Where(row => row.Id == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, listingStatus));
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
            return reservation;
        }

        public Task<Reservation> ReservationAsync(int id)
            => Database.Reservations.AsNoTracking().SingleAsync(row => row.Id == id);

        public Task<ListingStatus> ListingStatusAsync()
            => Database.Listings.AsNoTracking().Where(row => row.Id == 1).Select(row => row.Status).SingleAsync();

        public async Task<string> SnapshotAsync()
            => JsonSerializer.Serialize(new
            {
                Listings = await Database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                {
                    row.Id, row.Title, row.Description, row.Condition, row.Size, row.CategoryId, row.Status, row.CreatedAtUtc
                }).ToArrayAsync(),
                Reservations = await Database.Reservations.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                {
                    row.Id, row.UserId, row.ListingId, row.Status, row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason
                }).ToArrayAsync(),
                Audits = await Database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                {
                    row.Id, row.ListingId, row.ActorUserId, row.Action, row.OccurredAtUtc, row.Details
                }).ToArrayAsync()
            });

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
