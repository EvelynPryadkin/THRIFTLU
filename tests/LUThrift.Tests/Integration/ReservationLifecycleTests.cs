using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public sealed class ReservationLifecycleTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, ReservationOperationStatus.Canceled, ReservationStatus.Canceled)]
    [InlineData(0, ReservationOperationStatus.Expired, ReservationStatus.Expired)]
    [InlineData(1, ReservationOperationStatus.Expired, ReservationStatus.Expired)]
    public async Task Cancel_EnforcesExactDeadlineOutsidePickupHours(long ticks,
        ReservationOperationStatus operation, ReservationStatus persisted)
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddTicks(ticks));
        var reservation = await test.SeedAsync();

        var result = await test.Service.CancelAsync(reservation.Id, test.User);

        Assert.Equal(operation, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(persisted, (await test.ReservationAsync(reservation.Id)).Status);
        Assert.Equal(ListingStatus.Available, await test.ListingStatusAsync());
        var audit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(1, audit.ListingId);
        Assert.Equal(ticks < 0 ? Fixture.Student : ReservationService.ExpirationActorId, audit.ActorUserId);
        Assert.Equal(ticks < 0 ? InventoryAuditActions.ReservationCanceled : InventoryAuditActions.ReservationExpired, audit.Action);
        Assert.Equal(test.Clock.Now.UtcDateTime, audit.OccurredAtUtc);
        Assert.Contains($"Reservation {reservation.Id}", audit.Details);
        Assert.Contains("hold released", audit.Details);
        Assert.Equal(reservation.CreatedAtUtc, (await test.ReservationAsync(reservation.Id)).CreatedAtUtc);
        Assert.Equal(reservation.ExpiresAtUtc, (await test.ReservationAsync(reservation.Id)).ExpiresAtUtc);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Expire_EnforcesExactDeadlineWithoutConfiguredHours(long ticks, bool expired)
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddTicks(ticks));
        var reservation = await test.SeedAsync();
        var result = await test.Service.ExpireAsync(reservation.Id);
        Assert.Equal(expired ? ReservationOperationStatus.Expired : ReservationOperationStatus.NotDue, result.Status);
        Assert.Equal(expired ? ReservationStatus.Expired : ReservationStatus.Active, (await test.ReservationAsync(reservation.Id)).Status);
        Assert.Equal(expired ? ListingStatus.Available : ListingStatus.Held, await test.ListingStatusAsync());
        Assert.Equal(expired ? 1 : 0, await test.Database.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Cancel_UnknownAndOtherStudentsIdsAreIndistinguishable()
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddMinutes(-5));
        var reservation = await test.SeedAsync(userId: Fixture.OtherStudent);
        var other = await test.Service.CancelAsync(reservation.Id, test.User);
        var missing = await test.Service.CancelAsync(int.MaxValue, test.User);
        Assert.Equal(missing, other);
        Assert.Equal(ReservationOperationStatus.NotFound, other.Status);
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(reservation.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("anonymous", ReservationOperationStatus.AuthenticationRequired)]
    [InlineData("inactive", ReservationOperationStatus.StudentRequired)]
    [InlineData("not-student", ReservationOperationStatus.StudentRequired)]
    public async Task Cancel_RequiresCurrentActiveStudent(string scenario, ReservationOperationStatus expected)
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddMinutes(-5));
        var reservation = await test.SeedAsync();
        if (scenario == "inactive") await test.Database.Users.Where(row => row.Id == Fixture.Student)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        if (scenario == "not-student") await test.Database.UserRoles.ExecuteDeleteAsync();
        var result = await test.Service.CancelAsync(reservation.Id, scenario == "anonymous" ? null : test.User);
        Assert.Equal(expected, result.Status);
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(reservation.Id)).Status);
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Expire_StillReleasesInactiveAccountsReservations()
    {
        await using var test = await Fixture.CreateAsync(Deadline);
        var reservation = await test.SeedAsync();
        await test.Database.Users.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        await test.Database.UserRoles.ExecuteDeleteAsync();
        Assert.True((await test.Service.ExpireAsync(reservation.Id)).Changed);
        Assert.Equal(ListingStatus.Available, await test.ListingStatusAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedCancellationAndExpiration_AreNoOpsAfterTheFirstTransition(bool cancelFirst)
    {
        await using var test = await Fixture.CreateAsync(cancelFirst ? Deadline.AddMinutes(-1) : Deadline);
        var reservation = await test.SeedAsync();
        var first = cancelFirst ? await test.Service.CancelAsync(reservation.Id, test.User)
            : await test.Service.ExpireAsync(reservation.Id);
        Assert.True(first.Changed);
        test.Clock.Now = Deadline.AddHours(1);
        Assert.False((await test.Service.CancelAsync(reservation.Id, test.User)).Changed);
        Assert.False((await test.Service.ExpireAsync(reservation.Id)).Changed);
        Assert.Equal(new ReservationExpirationBatchResult(0, 0), await test.Service.ExpireBatchAsync(50));
        Assert.Single(await test.Database.AuditEvents.ToListAsync());
        Assert.Single(await test.Database.Reservations.ToListAsync());
        Assert.Equal(cancelFirst ? ReservationStatus.Canceled : ReservationStatus.Expired,
            (await test.ReservationAsync(reservation.Id)).Status);
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Available)]
    [InlineData(ListingStatus.Missing)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Archived)]
    public async Task Expire_DoesNotRestoreInventoryThatIsNotHeld(ListingStatus status)
    {
        await using var test = await Fixture.CreateAsync(Deadline);
        var reservation = await test.SeedAsync(listingStatus: status);
        Assert.True((await test.Service.ExpireAsync(reservation.Id)).Changed);
        Assert.Equal(status, await test.ListingStatusAsync());
        Assert.Contains("inventory unchanged", (await test.Database.AuditEvents.SingleAsync()).Details);
    }

    [Theory]
    [InlineData(ReservationStatus.Canceled)]
    [InlineData(ReservationStatus.Expired)]
    [InlineData(ReservationStatus.Fulfilled)]
    public async Task OldCompletedReservation_CannotReleaseANewerActiveHold(ReservationStatus oldStatus)
    {
        await using var test = await Fixture.CreateAsync(Deadline);
        var old = await test.SeedAsync(status: oldStatus);
        var newer = await test.SeedAsync(userId: Fixture.OtherStudent, deadline: Deadline.AddHours(1));
        Assert.False((await test.Service.CancelAsync(old.Id, test.User)).Changed);
        Assert.False((await test.Service.ExpireAsync(old.Id)).Changed);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(newer.Id)).Status);
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderActiveRow_DoesNotReleaseHoldWhenNewerHistoryExists(bool sameTimestamp)
    {
        await using var test = await Fixture.CreateAsync(Deadline);
        var old = await test.SeedAsync();
        var newer = await test.SeedAsync(userId: Fixture.OtherStudent, status: ReservationStatus.Canceled,
            deadline: sameTimestamp ? Deadline : Deadline.AddMinutes(10));
        Assert.True(newer.Id > old.Id);
        Assert.True((await test.Service.ExpireAsync(old.Id)).Changed);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        Assert.Contains("inventory unchanged", (await test.Database.AuditEvents.SingleAsync()).Details);
    }

    [Theory]
    [InlineData("reservation")]
    [InlineData("listing")]
    [InlineData("audit")]
    public async Task TransitionFailure_RollsBackReservationReleaseAndAudit(string failure)
    {
        await using var test = await Fixture.CreateAsync(Deadline);
        var reservation = await test.SeedAsync();
        var trigger = failure switch
        {
            "reservation" => "CREATE TRIGGER fail_write BEFORE UPDATE ON Reservations BEGIN SELECT RAISE(ABORT, 'test failure'); END;",
            "listing" => "CREATE TRIGGER fail_write BEFORE UPDATE OF Status ON Listings WHEN NEW.Status = 'Available' BEGIN SELECT RAISE(ABORT, 'test failure'); END;",
            _ => "CREATE TRIGGER fail_write BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'test failure'); END;"
        };
        await test.Database.Database.ExecuteSqlRawAsync(trigger);
        var result = await test.Service.ExpireAsync(reservation.Id);
        Assert.Equal(ReservationOperationStatus.PersistenceFailed, result.Status);
        Assert.DoesNotContain("test failure", result.Message);
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(reservation.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
        await test.Database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_write;");
        await test.Database.SaveChangesAsync();
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
        Assert.True((await test.Service.ExpireAsync(reservation.Id)).Changed);
    }

    [Fact]
    public async Task Batch_IsBoundedAndLeavesUnexpiredReservationsAlone()
    {
        await using var test = await Fixture.CreateAsync(Deadline);
        var oldest = await test.SeedAsync(deadline: Deadline.AddMinutes(-5));
        var due = await test.SeedAsync(listingId: 2);
        var future = await test.SeedAsync(listingId: 3, deadline: Deadline.AddTicks(1));
        Assert.Equal(new ReservationExpirationBatchResult(1, 0), await test.Service.ExpireBatchAsync(1));
        Assert.Equal(ReservationStatus.Expired, (await test.ReservationAsync(oldest.Id)).Status);
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(due.Id)).Status);
        Assert.Equal(new ReservationExpirationBatchResult(1, 0), await test.Service.ExpireBatchAsync(50));
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(future.Id)).Status);
        Assert.Equal(2, await test.Database.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Creation_ReconcilesOtherOwnersExpiredHoldAndPreservesHistory()
    {
        await using var test = await Fixture.CreateAsync(Deadline, configured: true);
        var old = await test.SeedAsync(userId: Fixture.OtherStudent);
        var result = await test.Service.CreateAsync(new CreateReservationInput { ListingId = 1 }, test.User);
        Assert.True(result.Succeeded);
        Assert.Equal(ReservationStatus.Expired, (await test.ReservationAsync(old.Id)).Status);
        var current = await test.ReservationAsync(result.ReservationId!.Value);
        Assert.Equal(Fixture.Student, current.UserId);
        Assert.Equal(ReservationStatus.Active, current.Status);
        Assert.Equal(Deadline.AddHours(1).UtcDateTime, current.ExpiresAtUtc);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        var audits = await test.Database.AuditEvents.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(new[] { InventoryAuditActions.ReservationExpired, InventoryAuditActions.Reserved }, audits.Select(row => row.Action));
    }

    [Fact]
    public async Task Creation_ReconciliationFailureCannotDiscardOldHold()
    {
        await using var test = await Fixture.CreateAsync(Deadline, configured: true);
        var old = await test.SeedAsync(userId: Fixture.OtherStudent);
        await test.Database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_audit BEFORE INSERT ON AuditEvents
            BEGIN SELECT RAISE(ABORT, 'test failure'); END;
            """);
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, test.User);
        Assert.Equal(ReservationCreationStatus.PersistenceFailed, result.Status);
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(old.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        Assert.Single(await test.Database.Reservations.ToListAsync());
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("not-student")]
    [InlineData("missing")]
    public async Task Creation_IneligibleCallerCannotTriggerReconciliation(string scenario)
    {
        await using var test = await Fixture.CreateAsync(Deadline, configured: true);
        var old = await test.SeedAsync(userId: Fixture.OtherStudent);
        if (scenario == "inactive") await test.Database.Users.Where(row => row.Id == Fixture.Student)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        if (scenario == "not-student") await test.Database.UserRoles.Where(row => row.UserId == Fixture.Student).ExecuteDeleteAsync();
        var caller = scenario == "missing" ? new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "missing-user")], "Test")) : test.User;
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, caller);
        Assert.Equal(ReservationCreationStatus.StudentRequired, result.Status);
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(old.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task AccountRead_ProjectsExpiredAtDeadlineWithoutWritesOrOtherOwners()
    {
        await using var test = await Fixture.CreateAsync(Deadline.AddTicks(-1));
        var own = await test.SeedAsync();
        await test.SeedAsync(listingId: 2, userId: Fixture.OtherStudent);
        var before = Assert.Single((await test.Service.GetForUserAsync(test.User)).Current);
        Assert.Equal(ReservationStatus.Active, before.Status);
        Assert.True(before.CanCancel);
        test.Clock.Now = Deadline;
        var at = Assert.Single((await test.Service.GetForUserAsync(test.User)).RecentHistory);
        Assert.Equal(ReservationStatus.Expired, at.Status);
        Assert.False(at.CanCancel);
        Assert.Equal(ReservationStatus.Active, (await test.ReservationAsync(own.Id)).Status);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
        var anonymous = await test.Service.GetForUserAsync(null);
        Assert.Empty(anonymous.Current);
        Assert.Empty(anonymous.RecentHistory);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext database,
        ReservationService service, Clock clock) : IAsyncDisposable
    {
        public const string Student = "lifecycle-student";
        public const string OtherStudent = "other-student";
        public ApplicationDbContext Database { get; } = database;
        public ReservationService Service { get; } = service;
        public Clock Clock { get; } = clock;
        public ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Student)], "Test"));

        public static async Task<Fixture> CreateAsync(DateTimeOffset now, bool configured = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var database = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await database.Database.EnsureCreatedAsync();
            database.Users.AddRange(new ApplicationUser { Id = Student, DisplayName = "Student" },
                new ApplicationUser { Id = OtherStudent, DisplayName = "Other student" });
            database.Roles.Add(new IdentityRole { Id = "role", Name = ApplicationRoles.Student });
            database.UserRoles.AddRange(new IdentityUserRole<string> { UserId = Student, RoleId = "role" },
                new IdentityUserRole<string> { UserId = OtherStudent, RoleId = "role" });
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            var clock = new Clock(now);
            var options = new ReservationOptions
            {
                CampusTimeZone = "UTC",
                WeeklyPickupIntervals = configured ? [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "17:00" }] : []
            };
            return new(connection, database, new ReservationService(database,
                new PickupHoursEligibility(Options.Create(options), clock), NullLogger<ReservationService>.Instance, clock), clock);
        }

        public async Task<Reservation> SeedAsync(int listingId = 1, string userId = Student,
            DateTimeOffset? deadline = null, ReservationStatus status = ReservationStatus.Active,
            ListingStatus listingStatus = ListingStatus.Held)
        {
            var expires = deadline ?? Deadline;
            var reservation = new Reservation
            {
                ListingId = listingId, UserId = userId, CreatedAtUtc = expires.AddHours(-1).UtcDateTime,
                ExpiresAtUtc = expires.UtcDateTime, Status = status
            };
            Database.Reservations.Add(reservation);
            await Database.Listings.Where(row => row.Id == listingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, listingStatus));
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
            return reservation;
        }

        public Task<Reservation> ReservationAsync(int id) => Database.Reservations.AsNoTracking().SingleAsync(row => row.Id == id);
        public Task<ListingStatus> ListingStatusAsync() => Database.Listings.AsNoTracking().Where(row => row.Id == 1).Select(row => row.Status).SingleAsync();
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
