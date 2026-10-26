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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Unit;

public class ReservationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_UsesTrustedIdentityAndServerTimeDespiteUnexpectedInputFields()
    {
        await using var test = await TestContext.CreateAsync();
        var input = JsonSerializer.Deserialize<CreateReservationInput>("""
            {"ListingId":1,"Id":999,"UserId":"forged-user","Role":"Administrator",
             "Status":"Fulfilled","CreatedAtUtc":"2000-01-01T00:00:00Z",
             "ExpiresAtUtc":"2099-01-01T00:00:00Z","AuditEvents":[{"Action":"forged"}]}
            """)!;

        var result = await test.Service.CreateAsync(input, test.User);

        Assert.True(result.Succeeded);
        var reservation = await test.Database.Reservations.AsNoTracking().SingleAsync();
        Assert.Equal(result.ReservationId, reservation.Id);
        Assert.NotEqual(999, reservation.Id);
        Assert.Equal(TestContext.UserId, reservation.UserId);
        Assert.Equal(1, reservation.ListingId);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(Now.UtcDateTime, reservation.CreatedAtUtc);
        Assert.Equal(Now.AddHours(1).UtcDateTime, reservation.ExpiresAtUtc);
        Assert.Equal(reservation.ExpiresAtUtc, result.ExpiresAtUtc);
        Assert.Null(reservation.FulfilledAtUtc);
        Assert.Null(reservation.CancellationReason);
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
        var audit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(reservation.ListingId, audit.ListingId);
        Assert.Equal(TestContext.UserId, audit.ActorUserId);
        Assert.Equal(InventoryAuditActions.Reserved, audit.Action);
        Assert.Equal(reservation.CreatedAtUtc, audit.OccurredAtUtc);
        Assert.Equal("Status changed from Available to Held by reservation creation.", audit.Details);
        Assert.Equal(nameof(CreateReservationInput.ListingId), Assert.Single(typeof(CreateReservationInput).GetProperties()).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_RejectsInvalidInput(int? listingId)
    {
        await using var test = await TestContext.CreateAsync();
        var result = await test.Service.CreateAsync(
            listingId.HasValue ? new() { ListingId = listingId.Value } : null, test.User);
        Assert.Equal(ReservationCreationStatus.InvalidInput, result.Status);
        await test.AssertUnchangedAsync();
    }

    [Theory]
    [InlineData(false, "student")]
    [InlineData(true, null)]
    [InlineData(true, " ")]
    public async Task Create_RequiresAuthenticatedIdentity(bool authenticated, string? id)
    {
        await using var test = await TestContext.CreateAsync();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            id is null ? [] : [new Claim(ClaimTypes.NameIdentifier, id)], authenticated ? "Test" : null));
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, user);
        Assert.Equal(ReservationCreationStatus.AuthenticationRequired, result.Status);
        await test.AssertUnchangedAsync();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("inactive")]
    [InlineData("not-student")]
    public async Task Create_ChecksCurrentAccountAndMembershipInsteadOfRoleClaims(string scenario)
    {
        await using var test = await TestContext.CreateAsync();
        var userId = TestContext.UserId;
        if (scenario == "unknown") userId = "unknown";
        if (scenario == "inactive")
            await test.Database.Users.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        if (scenario == "not-student")
            await test.Database.UserRoles.ExecuteDeleteAsync();
        var forgedClaims = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, ApplicationRoles.Student)
        ], "Test"));

        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, forgedClaims);
        Assert.Equal(ReservationCreationStatus.StudentRequired, result.Status);
        await test.AssertUnchangedAsync();
    }

    [Fact]
    public async Task Create_UnknownListingReturnsNotFound()
    {
        await using var test = await TestContext.CreateAsync();
        var result = await test.Service.CreateAsync(new() { ListingId = int.MaxValue }, test.User);
        Assert.Equal(ReservationCreationStatus.NotFound, result.Status);
        await test.AssertUnchangedAsync();
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Held)]
    [InlineData(ListingStatus.Missing)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Archived)]
    public async Task Create_RejectsUnavailableListing(ListingStatus status)
    {
        await using var test = await TestContext.CreateAsync();
        await test.Database.Listings.Where(row => row.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, status));
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, test.User);
        Assert.Equal(ReservationCreationStatus.Unavailable, result.Status);
        await test.AssertUnchangedAsync(status);
    }

    [Fact]
    public async Task Create_DuplicateSubmissionRetainsOneReservationAndAudit()
    {
        await using var test = await TestContext.CreateAsync();
        Assert.True((await test.Service.CreateAsync(new() { ListingId = 1 }, test.User)).Succeeded);
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, test.User);
        Assert.Equal(ReservationCreationStatus.AlreadyReserved, result.Status);
        Assert.Single(await test.Database.Reservations.ToListAsync());
        Assert.Single(await test.Database.AuditEvents.ToListAsync());
        Assert.Equal(ListingStatus.Held, await test.ListingStatusAsync());
    }

    [Theory]
    [InlineData(2, ReservationCreationStatus.Success)]
    [InlineData(3, ReservationCreationStatus.LimitReached)]
    public async Task Create_EnforcesThreeActiveLimit(int existingCount, ReservationCreationStatus expected)
    {
        await using var test = await TestContext.CreateAsync();
        for (var index = 0; index < existingCount; index++)
            await test.SeedReservationAsync(index + 2, ReservationStatus.Active, Now.AddMinutes(15));
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, test.User);
        Assert.Equal(expected, result.Status);
        Assert.Equal(existingCount + (result.Succeeded ? 1 : 0), await test.Database.Reservations.CountAsync());
        Assert.Equal(result.Succeeded ? 1 : 0, await test.Database.AuditEvents.CountAsync());
        Assert.Equal(result.Succeeded ? ListingStatus.Held : ListingStatus.Available, await test.ListingStatusAsync());
    }

    [Theory]
    [InlineData(ReservationStatus.Active, 0)]
    [InlineData(ReservationStatus.Active, -1)]
    [InlineData(ReservationStatus.Canceled, 15)]
    [InlineData(ReservationStatus.Expired, 15)]
    [InlineData(ReservationStatus.Fulfilled, 15)]
    public async Task Create_CountsOnlyActiveReservationsStrictlyBeforeTheirDeadline(
        ReservationStatus status, int deadlineMinutes)
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedReservationAsync(2, ReservationStatus.Active, Now.AddMinutes(15));
        await test.SeedReservationAsync(3, ReservationStatus.Active, Now.AddMinutes(15));
        await test.SeedReservationAsync(4, status, Now.AddMinutes(deadlineMinutes));
        Assert.True((await test.Service.CreateAsync(new() { ListingId = 1 }, test.User)).Succeeded);
        Assert.Equal(4, await test.Database.Reservations.CountAsync());
    }

    [Fact]
    public async Task Create_DoesNotReconcileAnExpiredStoredActiveReservationYet()
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedReservationAsync(1, ReservationStatus.Active, Now);
        // Also protect inconsistent legacy data where the listing was released early.
        await test.Database.Listings.Where(row => row.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Available));
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, test.User);
        Assert.Equal(ReservationCreationStatus.Unavailable, result.Status);
        Assert.Single(await test.Database.Reservations.ToListAsync());
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
        Assert.Equal(ListingStatus.Available, await test.ListingStatusAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_UnconfiguredOrClosedHoursDoNotWrite(bool closed)
    {
        await using var test = await TestContext.CreateAsync(
            closed ? new SequenceClock(Now.AddHours(6)) : null, unconfigured: !closed);
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, test.User);
        Assert.Equal(ReservationCreationStatus.PickupUnavailable, result.Status);
        await test.AssertUnchangedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_RechecksTimeBeforeSavingAndRollsBackHold(bool backwards)
    {
        var start = Now.AddHours(4); // Exactly one hour before closing.
        await using var test = await TestContext.CreateAsync(new SequenceClock(start,
            backwards ? start.AddSeconds(-1) : start.AddTicks(1)));
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, test.User);
        Assert.Equal(backwards ? ReservationCreationStatus.Conflict : ReservationCreationStatus.PickupUnavailable, result.Status);
        await test.AssertUnchangedAsync();
    }

    [Fact]
    public async Task Create_UsesFreshFinalClockForBothTimestamps()
    {
        await using var test = await TestContext.CreateAsync(new SequenceClock(Now, Now.AddSeconds(5)));
        Assert.True((await test.Service.CreateAsync(new() { ListingId = 1 }, test.User)).Succeeded);
        var reservation = await test.Database.Reservations.SingleAsync();
        Assert.Equal(Now.AddSeconds(5).UtcDateTime, reservation.CreatedAtUtc);
        Assert.Equal(Now.AddHours(1).AddSeconds(5).UtcDateTime, reservation.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("AuditEvents")]
    [InlineData("Reservations")]
    public async Task Create_PersistenceFailureRollsBackAllWritesAndDetachesFailedAdditions(string table)
    {
        await using var test = await TestContext.CreateAsync();
        var trigger = table == "AuditEvents" ? """
            CREATE TRIGGER RejectInsert BEFORE INSERT ON "AuditEvents"
            BEGIN SELECT RAISE(ABORT, 'Synthetic persistence failure'); END;
            """ : """
            CREATE TRIGGER RejectInsert BEFORE INSERT ON "Reservations"
            BEGIN SELECT RAISE(ABORT, 'Synthetic persistence failure'); END;
            """;
        await test.Database.Database.ExecuteSqlRawAsync(trigger);
        var result = await test.Service.CreateAsync(new() { ListingId = 1 }, test.User);
        Assert.Equal(ReservationCreationStatus.PersistenceFailed, result.Status);
        Assert.Null(result.ReservationId);
        Assert.DoesNotContain("Synthetic", result.Message);
        await test.AssertUnchangedAsync();
        Assert.Empty(test.Database.ChangeTracker.Entries<Reservation>());
        Assert.Empty(test.Database.ChangeTracker.Entries<AuditEvent>());
        await test.Database.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectInsert;");
        await test.Database.SaveChangesAsync();
        await test.AssertUnchangedAsync();
        Assert.True((await test.Service.CreateAsync(new() { ListingId = 1 }, test.User)).Succeeded);
    }

    [Fact]
    public async Task Create_ChecksPersistedListingStatusDespiteTrackedAvailableEntity()
    {
        await using var test = await TestContext.CreateAsync();
        var stale = await test.Database.Listings.SingleAsync(row => row.Id == 1);
        await test.Database.Listings.Where(row => row.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Archived));
        Assert.Equal(ListingStatus.Available, stale.Status);
        Assert.Equal(ReservationCreationStatus.Unavailable,
            (await test.Service.CreateAsync(new() { ListingId = 1 }, test.User)).Status);
        await test.AssertUnchangedAsync(ListingStatus.Archived);
    }

    private sealed class SequenceClock(params DateTimeOffset[] times) : TimeProvider
    {
        private int position;
        public override DateTimeOffset GetUtcNow() => times[Math.Min(position++, times.Length - 1)];
    }

    private sealed class TestContext(SqliteConnection connection, ApplicationDbContext database,
        ReservationService service) : IAsyncDisposable
    {
        public const string UserId = "reservation-student";
        public ApplicationDbContext Database { get; } = database;
        public ReservationService Service { get; } = service;
        public ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId)], "Test"));

        public static async Task<TestContext> CreateAsync(TimeProvider? clock = null, bool unconfigured = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var database = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).Options);
            await database.Database.EnsureCreatedAsync();
            database.Users.Add(new ApplicationUser { Id = UserId, DisplayName = "Reservation test student" });
            database.Roles.Add(new IdentityRole { Id = "student-role", Name = ApplicationRoles.Student });
            database.UserRoles.Add(new IdentityUserRole<string> { UserId = UserId, RoleId = "student-role" });
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            var options = new ReservationOptions
            {
                CampusTimeZone = "UTC",
                WeeklyPickupIntervals = unconfigured ? [] :
                    [new() { Day = "Monday", OpensAt = "09:00", ClosesAt = "17:00" }]
            };
            var service = new ReservationService(database,
                new PickupHoursEligibility(Options.Create(options), clock ?? new SequenceClock(Now)),
                NullLogger<ReservationService>.Instance);
            return new(connection, database, service);
        }

        public async Task SeedReservationAsync(int listingId, ReservationStatus status, DateTimeOffset deadline)
        {
            Database.Reservations.Add(new Reservation
            {
                UserId = UserId, ListingId = listingId, Status = status,
                CreatedAtUtc = deadline.AddHours(-1).UtcDateTime, ExpiresAtUtc = deadline.UtcDateTime
            });
            await Database.Listings.Where(row => row.Id == listingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Held));
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
        }

        public Task<ListingStatus> ListingStatusAsync() => Database.Listings.AsNoTracking()
            .Where(row => row.Id == 1).Select(row => row.Status).SingleAsync();

        public async Task AssertUnchangedAsync(ListingStatus status = ListingStatus.Available)
        {
            Assert.Equal(status, await ListingStatusAsync());
            Assert.Empty(await Database.Reservations.AsNoTracking().ToListAsync());
            Assert.Empty(await Database.AuditEvents.AsNoTracking().ToListAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
