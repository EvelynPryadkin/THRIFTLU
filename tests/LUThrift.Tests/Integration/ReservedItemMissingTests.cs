using System.Security.Claims;
using System.Text.Json;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class ReservedItemMissingTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 10, 26, 11, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task ReportMissing_CancelsTheHoldAndMarksInventoryMissingWithOneStaffAudit(string role)
    {
        await using var test = await Fixture.CreateAsync(role: role);
        var original = await test.SeedAsync();
        Assert.Empty(test.Actor.FindAll(ClaimTypes.Role));

        var result = await test.Service.ReportMissingAsync(original.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.Canceled, result.Status);
        Assert.True(result.Changed);
        var saved = await test.ReservationAsync(original.Id);
        Assert.Equal(ReservationStatus.Canceled, saved.Status);
        Assert.Equal(ReservationCancellationReasons.ItemNotFound, saved.CancellationReason);
        Assert.Null(saved.FulfilledAtUtc);
        Assert.Equal(original.UserId, saved.UserId);
        Assert.Equal(original.ListingId, saved.ListingId);
        Assert.Equal(original.CreatedAtUtc, saved.CreatedAtUtc);
        Assert.Equal(original.ExpiresAtUtc, saved.ExpiresAtUtc);
        Assert.Equal(ListingStatus.Missing, await test.ListingStatusAsync(original.ListingId));
        var audit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(InventoryAuditActions.ReservationItemMissing, audit.Action);
        Assert.Equal(Fixture.Staff, audit.ActorUserId);
        Assert.NotEqual(saved.UserId, audit.ActorUserId);
        Assert.Equal(saved.ListingId, audit.ListingId);
        Assert.Equal(test.Clock.Now.UtcDateTime, audit.OccurredAtUtc);
        Assert.Contains($"Reservation {saved.Id}", audit.Details);
        Assert.Contains("Active", audit.Details);
        Assert.Contains("Canceled", audit.Details);
        Assert.Contains("Held", audit.Details);
        Assert.Contains("Missing", audit.Details);
        Assert.DoesNotContain("@", audit.Details);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task ReportMissing_RejectsTheExactDeadlineWithoutPersistingExpiration(long ticks, bool succeeds)
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddTicks(ticks));
        var reservation = await test.SeedAsync();
        var before = await test.SnapshotAsync();

        var result = await test.Service.ReportMissingAsync(reservation.Id, test.Actor);

        Assert.Equal(succeeds ? ReservationOperationStatus.Canceled : ReservationOperationStatus.DeadlinePassed, result.Status);
        Assert.Equal(succeeds, result.Changed);
        if (!succeeds)
        {
            Assert.Equal(before, await test.SnapshotAsync());
            Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(reservation.Id)).Status);
            Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        }
    }

    [Fact]
    public async Task ReportMissing_RechecksTimeImmediatelyBeforeChangingTheReservation()
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        var before = await test.SnapshotAsync();
        test.Clock.SetReadSequence(Deadline.AddTicks(-1), Deadline);

        var result = await test.Service.ReportMissingAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.DeadlinePassed, result.Status);
        Assert.False(result.Changed);
        Assert.True(test.Clock.ReadCount >= 2);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportMissing_DoesNotApplyTheNewReservationHoursRule(bool configured)
    {
        var closing = new DateTimeOffset(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);
        await using var test = await Fixture.CreateAsync(closing.AddSeconds(-10), configured: configured);
        var reservation = await test.SeedAsync(deadline: closing);
        Assert.Equal(configured ? PickupHoursStatus.InsufficientTime : PickupHoursStatus.ScheduleUnavailable,
            test.Hours.Evaluate().Status);
        test.Clock.SetReadSequence(closing.AddSeconds(-10), closing.AddSeconds(-1));

        var result = await test.Service.ReportMissingAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.Canceled, result.Status);
        Assert.Equal(ListingStatus.Missing, await test.ListingStatusAsync());
        Assert.Equal(closing.AddSeconds(-1).UtcDateTime, (await test.Database.AuditEvents.AsNoTracking().SingleAsync()).OccurredAtUtc);
    }

    [Theory]
    [InlineData("anonymous", ReservationOperationStatus.AuthenticationRequired)]
    [InlineData("missing identifier", ReservationOperationStatus.AuthenticationRequired)]
    [InlineData("unknown account", ReservationOperationStatus.StaffRequired)]
    [InlineData("inactive", ReservationOperationStatus.StaffRequired)]
    [InlineData("revoked", ReservationOperationStatus.StaffRequired)]
    [InlineData("student with forged staff claim", ReservationOperationStatus.StaffRequired)]
    public async Task ReportMissing_RequiresCurrentActiveStaffMembership(string scenario, ReservationOperationStatus expected)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        if (scenario == "inactive") await test.Database.Users.Where(row => row.Id == Fixture.Staff)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        if (scenario == "revoked") await test.Database.UserRoles.Where(row => row.UserId == Fixture.Staff).ExecuteDeleteAsync();
        var principal = scenario switch
        {
            "anonymous" => null,
            "missing identifier" => new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, ApplicationRoles.Staff)], "Test")),
            "unknown account" => Principal("unknown-staff"),
            "student with forged staff claim" => new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Fixture.Student), new Claim(ClaimTypes.Role, ApplicationRoles.Staff)], "Test")),
            _ => test.Actor
        };
        var before = await test.SnapshotAsync();

        var result = await test.Service.ReportMissingAsync(reservation.Id, principal);
        var unknown = await test.Service.ReportMissingAsync(int.MaxValue, principal);

        Assert.Equal(expected, result.Status);
        Assert.Equal(result, unknown);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task ReportMissing_UnknownOrInvalidIdsAreSafe(int id)
    {
        await using var test = await Fixture.CreateAsync();
        await test.SeedAsync();
        var before = await test.SnapshotAsync();

        var result = await test.Service.ReportMissingAsync(id, test.Actor);

        Assert.Equal(ReservationOperationStatus.NotFound, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ReservationStatus.Canceled)]
    [InlineData(ReservationStatus.Expired)]
    [InlineData(ReservationStatus.Fulfilled)]
    public async Task ReportMissing_DoesNotRewriteTerminalReservationHistory(ReservationStatus status)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync(status: status);
        var before = await test.SnapshotAsync();

        var result = await test.Service.ReportMissingAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.NotActive, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Available)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Missing)]
    [InlineData(ListingStatus.Archived)]
    public async Task ReportMissing_RequiresTheListingToStillBeHeld(ListingStatus status)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync(listingStatus: status);
        var before = await test.SnapshotAsync();

        var result = await test.Service.ReportMissingAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.InvalidState, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportMissing_NeverChangesAReplacementHold(bool originalStillActive)
    {
        await using var test = await Fixture.CreateAsync();
        var original = await test.SeedAsync(status: originalStillActive ? ReservationStatus.Active : ReservationStatus.Canceled);
        if (originalStillActive)
            await test.Database.Database.ExecuteSqlRawAsync("DROP INDEX IX_Reservations_ActiveListing;");
        var newer = new Reservation
        {
            UserId = Fixture.OtherStudent, ListingId = original.ListingId, Status = ReservationStatus.Active,
            CreatedAtUtc = original.CreatedAtUtc.AddMinutes(1), ExpiresAtUtc = original.ExpiresAtUtc.AddMinutes(1)
        };
        test.Database.Reservations.Add(newer);
        await test.Database.SaveChangesAsync();
        test.Database.ChangeTracker.Clear();
        var before = await test.SnapshotAsync();

        var result = await test.Service.ReportMissingAsync(original.Id, test.Actor);

        Assert.Equal(originalStillActive ? ReservationOperationStatus.InvalidState : ReservationOperationStatus.NotActive, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(newer.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
    }

    [Fact]
    public async Task ReportMissing_RepeatsAndLaterCancellationOrExpirationPreserveMissingInventory()
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        Assert.True((await test.Service.ReportMissingAsync(reservation.Id, test.Actor)).Changed);
        var before = await test.SnapshotAsync();
        test.Clock.Now = Deadline.AddHours(1);

        var repeated = await test.Service.ReportMissingAsync(reservation.Id, test.Actor);
        var pickup = await test.Service.ConfirmPickupAsync(reservation.Id, test.Actor);
        var canceled = await test.Service.CancelAsync(reservation.Id, Principal(Fixture.Student));
        var expired = await test.Service.ExpireAsync(reservation.Id);
        var batch = await test.Service.ExpireBatchAsync(10);

        Assert.Equal(ReservationOperationStatus.NotActive, repeated.Status);
        Assert.False(repeated.Changed);
        Assert.False(pickup.Changed);
        Assert.False(canceled.Changed);
        Assert.False(expired.Changed);
        Assert.Equal(new ReservationExpirationBatchResult(0, 0), batch);
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.Equal(ListingStatus.Missing, await test.ListingStatusAsync());
        Assert.Equal(ReservationCancellationReasons.ItemNotFound, (await test.ReservationAsync(reservation.Id)).CancellationReason);
        Assert.Single(await test.Database.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("reservation")]
    [InlineData("listing")]
    [InlineData("audit")]
    public async Task ReportMissing_WriteFailuresRollbackTheWholeTransitionAndLeaveNoDeferredAudit(string failure)
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        var trigger = failure switch
        {
            "reservation" => "CREATE TRIGGER fail_missing BEFORE UPDATE ON Reservations WHEN NEW.Status = 'Canceled' BEGIN SELECT RAISE(ABORT, 'private database failure'); END;",
            "listing" => "CREATE TRIGGER fail_missing BEFORE UPDATE OF Status ON Listings WHEN NEW.Status = 'Missing' BEGIN SELECT RAISE(ABORT, 'private database failure'); END;",
            _ => "CREATE TRIGGER fail_missing BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'private database failure'); END;"
        };
        await test.Database.Database.ExecuteSqlRawAsync(trigger);
        var before = await test.SnapshotAsync();

        var result = await test.Service.ReportMissingAsync(reservation.Id, test.Actor);

        Assert.Equal(ReservationOperationStatus.PersistenceFailed, result.Status);
        Assert.False(result.Changed);
        Assert.DoesNotContain("private database failure", result.Message);
        Assert.All(test.Logger.Entries, entry => Assert.DoesNotContain("private database failure", entry.Message));
        Assert.Equal(before, await test.SnapshotAsync());
        await test.Database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_missing;");
        await test.Database.SaveChangesAsync();
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.True((await test.Service.ReportMissingAsync(reservation.Id, test.Actor)).Changed);
        Assert.Single(await test.Database.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task ReportMissing_FreesOnlyItsOwnersSlotAndNeverMakesTheMissingItemReservable()
    {
        await using var test = await Fixture.CreateAsync(configured: true);
        var missing = await test.SeedAsync();
        await test.SeedAsync(listingId: 2);
        await test.SeedAsync(listingId: 3);
        var otherReservation = await test.SeedAsync(listingId: 4, owner: Fixture.OtherStudent);
        var available = new Listing
        {
            Title = "Replacement item", Description = "Synthetic missing-item test fixture", Condition = "Good",
            CategoryId = 1, Status = ListingStatus.Available, CreatedAtUtc = Deadline.AddDays(-1).UtcDateTime
        };
        test.Database.Listings.Add(available);
        await test.Database.SaveChangesAsync();
        test.Database.ChangeTracker.Clear();
        var blocked = await test.Service.CreateAsync(new CreateReservationInput { ListingId = available.Id }, Principal(Fixture.Student));
        Assert.Equal(ReservationCreationStatus.LimitReached, blocked.Status);
        Assert.True((await test.Service.ReportMissingAsync(missing.Id, test.Actor)).Changed);

        var ownerOverview = await test.Service.GetForUserAsync(Principal(Fixture.Student));
        var otherOverview = await test.Service.GetForUserAsync(Principal(Fixture.OtherStudent));
        Assert.Equal(2, ownerOverview.ActiveCount);
        Assert.Equal(missing.Id, Assert.Single(ownerOverview.RecentHistory).Id);
        Assert.Equal(otherReservation.Id, Assert.Single(otherOverview.Current).Id);
        Assert.Empty(otherOverview.RecentHistory);
        var replacement = await test.Service.CreateAsync(new CreateReservationInput { ListingId = available.Id }, Principal(Fixture.Student));
        Assert.True(replacement.Succeeded);
        var cannotReserveMissing = await test.Service.CreateAsync(new CreateReservationInput { ListingId = missing.ListingId }, Principal(Fixture.OtherStudent));
        Assert.Equal(ReservationCreationStatus.Unavailable, cannotReserveMissing.Status);
        Assert.Equal(ListingStatus.Missing, await test.ListingStatusAsync(missing.ListingId));
        Assert.Equal(ReservationStatus.Canceled, (await test.ReservationAsync(missing.Id)).Status);
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(otherReservation.Id)).Status);
        Assert.Equal(3, (await test.Service.GetForUserAsync(Principal(Fixture.Student))).ActiveCount);
        var audits = await test.Database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(new[] { InventoryAuditActions.ReservationItemMissing, InventoryAuditActions.Reserved }, audits.Select(row => row.Action));
    }

    [Fact]
    public async Task OrdinaryInventoryMarkMissingStillRejectsHeldAndHandlesOnlyAvailableInventory()
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        var listingService = new ListingService(test.Database);
        var before = await test.SnapshotAsync();

        var held = await listingService.MarkMissingAsync(reservation.ListingId, Fixture.Staff);

        Assert.Equal(ListingResultStatus.InvalidTransition, held.Status);
        Assert.Equal(before, await test.SnapshotAsync());
        Assert.True((await listingService.MarkMissingAsync(2, Fixture.Staff)).Succeeded);
        Assert.Equal(ListingStatus.Missing, await test.ListingStatusAsync(2));
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(reservation.Id)).Status);
        Assert.Equal(InventoryAuditActions.MarkedMissing, (await test.Database.AuditEvents.AsNoTracking().SingleAsync()).Action);
        Assert.True((await test.Service.ReportMissingAsync(reservation.Id, test.Actor)).Changed);
        Assert.Equal(new[] { InventoryAuditActions.MarkedMissing, InventoryAuditActions.ReservationItemMissing },
            await test.Database.AuditEvents.OrderBy(row => row.Id).Select(row => row.Action).ToArrayAsync());
    }

    [Fact]
    public async Task ReportMissing_CancellationTokenPropagatesWithoutChanges()
    {
        await using var test = await Fixture.CreateAsync();
        var reservation = await test.SeedAsync();
        var before = await test.SnapshotAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => test.Service.ReportMissingAsync(reservation.Id, test.Actor, cancellation.Token));

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

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext database, ReservationService service,
        PickupHoursEligibility hours, Clock clock, RecordingLogger<ReservationService> logger) : IAsyncDisposable
    {
        public const string Staff = "missing-staff";
        public const string Student = "missing-owner";
        public const string OtherStudent = "missing-other-owner";
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
            database.Users.AddRange(new ApplicationUser { Id = Staff, DisplayName = "Missing-item staff" },
                new ApplicationUser { Id = Student, DisplayName = "Reservation owner" },
                new ApplicationUser { Id = OtherStudent, DisplayName = "Other owner" });
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
                // Synthetic test configuration, not official thrift-room pickup hours.
                WeeklyPickupIntervals = configured ? [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "17:00" }] : []
            }), clock);
            var logger = new RecordingLogger<ReservationService>();
            return new(connection, database, new ReservationService(database, hours, logger, clock), hours, clock, logger);
        }

        public async Task<Reservation> SeedAsync(int listingId = 1, string owner = Student,
            ReservationStatus status = ReservationStatus.Active, ListingStatus listingStatus = ListingStatus.Held,
            DateTimeOffset? deadline = null)
        {
            var expires = deadline ?? Deadline;
            var reservation = new Reservation
            {
                ListingId = listingId, UserId = owner, Status = status,
                CreatedAtUtc = expires.AddHours(-1).UtcDateTime, ExpiresAtUtc = expires.UtcDateTime,
                FulfilledAtUtc = status == ReservationStatus.Fulfilled ? expires.AddMinutes(-20).UtcDateTime : null,
                CancellationReason = status == ReservationStatus.Canceled ? "Canceled by student." : null
            };
            Database.Reservations.Add(reservation);
            await Database.Listings.Where(row => row.Id == listingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, listingStatus));
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
            return reservation;
        }
        public Task<Reservation> ReservationAsync(int id)
            => Database.Reservations.AsNoTracking().SingleAsync(row => row.Id == id);
        public Task<ListingStatus> ListingStatusAsync(int id = 1)
            => Database.Listings.AsNoTracking().Where(row => row.Id == id).Select(row => row.Status).SingleAsync();
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
