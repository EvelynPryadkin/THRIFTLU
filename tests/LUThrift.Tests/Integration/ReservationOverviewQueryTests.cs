using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

public class ReservationOverviewQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Overview_SeparatesCurrentFromBoundedHistoryAndFiltersOwnershipBeforeTheLimit()
    {
        await using var test = await TestContext.CreateAsync(Now);
        var history = Enumerable.Range(0, 25).Select(index => test.Reservation(
            100 + index, TestContext.OwnerId, index % 2 == 0 ? ReservationStatus.Canceled : ReservationStatus.Fulfilled,
            Now.AddMinutes(-5 - index / 3), Now.AddMinutes(55 - index / 3))).ToArray();
        var current = Enumerable.Range(0, 3).Select(index => test.Reservation(
            200 + index, TestContext.OwnerId, ReservationStatus.Active, Now.AddMinutes(-40), Now.AddMinutes(20))).ToArray();
        var otherOwnerHistory = Enumerable.Range(0, 30).Select(index => test.Reservation(
            300 + index, TestContext.OtherOwnerId, ReservationStatus.Canceled,
            Now.AddMinutes(-1).AddTicks(index), Now.AddMinutes(59))).ToArray();
        var otherOwnerCurrent = test.Reservation(330, TestContext.OtherOwnerId, ReservationStatus.Active,
            Now.AddMinutes(-1), Now.AddMinutes(59));
        await test.SeedAsync([.. history, .. current, .. otherOwnerHistory, otherOwnerCurrent]);
        var before = await test.SnapshotAsync();
        test.Commands.Clear();

        var overview = await test.Service.GetForUserAsync(test.Owner);

        Assert.Equal(3, overview.ActiveCount);
        Assert.Equal(current.OrderByDescending(row => row.Id).Select(row => row.Id),
            overview.Current.Select(row => row.Id));
        Assert.All(overview.Current, row =>
        {
            Assert.Equal(ReservationStatus.Active, row.Status);
            Assert.True(row.CanCancel);
        });
        Assert.Equal(20, overview.RecentHistory.Count);
        var expectedHistory = history.OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id).Take(20).ToArray();
        Assert.Equal(expectedHistory.Select(row => row.Id), overview.RecentHistory.Select(row => row.Id));
        Assert.Equal(expectedHistory.Select(row => row.Status), overview.RecentHistory.Select(row => row.Status));
        Assert.All(overview.RecentHistory, row => Assert.False(row.CanCancel));
        Assert.Empty(overview.Current.Select(row => row.Id).Intersect(overview.RecentHistory.Select(row => row.Id)));
        Assert.Equal(1, test.Clock.ReadCount);
        var reservationSql = Assert.Single(test.Commands.Commands,
            sql => sql.Contains("\"Reservations\"", StringComparison.Ordinal));
        Assert.Contains("LIMIT", reservationSql, StringComparison.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(overview);
        Assert.DoesNotContain(TestContext.OwnerId, json);
        Assert.DoesNotContain(TestContext.OtherOwnerId, json);
        Assert.DoesNotContain("UserId", json);
        Assert.DoesNotContain("CancellationReason", json);
        Assert.DoesNotContain("Private student name", json);
        await test.AssertUnchangedAsync(before);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task DeadlineBoundary_UsesOneServerInstantAndProjectsExpiryWithoutPersistingIt(long offsetTicks, bool current)
    {
        await using var test = await TestContext.CreateAsync(Now.AddTicks(offsetTicks));
        var reservation = test.Reservation(101, TestContext.OwnerId, ReservationStatus.Active, Now.AddHours(-1), Now);
        await test.SeedAsync([reservation]);
        var before = await test.SnapshotAsync();

        var overview = await test.Service.GetForUserAsync(test.Owner);

        Assert.Equal(current ? 1 : 0, overview.ActiveCount);
        Assert.Equal(current ? 1 : 0, overview.Current.Count);
        Assert.Equal(current ? 0 : 1, overview.RecentHistory.Count);
        var summary = Assert.Single(current ? overview.Current : overview.RecentHistory);
        Assert.Equal(reservation.Id, summary.Id);
        Assert.Equal(current ? ReservationStatus.Active : ReservationStatus.Expired, summary.Status);
        Assert.Equal(current, summary.CanCancel);
        Assert.Equal(reservation.CreatedAtUtc, summary.CreatedAtUtc);
        Assert.Equal(reservation.ExpiresAtUtc, summary.ExpiresAtUtc);
        Assert.Equal(1, test.Clock.ReadCount);
        await test.AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task CurrentList_DoesNotHideALegacyReservationAboveTheCreationLimit()
    {
        await using var test = await TestContext.CreateAsync(Now);
        var reservations = Enumerable.Range(0, 4).Select(index => test.Reservation(
            100 + index, TestContext.OwnerId, ReservationStatus.Active,
            Now.AddMinutes(-30 + index), Now.AddMinutes(30 + index))).ToArray();
        await test.SeedAsync(reservations);
        var before = await test.SnapshotAsync();

        var overview = await test.Service.GetForUserAsync(test.Owner);

        Assert.Equal(4, overview.ActiveCount);
        Assert.Equal(reservations.OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id)
            .Select(row => row.Id), overview.Current.Select(row => row.Id));
        Assert.Empty(overview.RecentHistory);
        await test.AssertUnchangedAsync(before);
    }

    [Theory]
    [InlineData("null principal")]
    [InlineData("anonymous")]
    [InlineData("missing identifier")]
    [InlineData("blank identifier")]
    [InlineData("unknown account")]
    [InlineData("inactive account")]
    [InlineData("missing student membership")]
    public async Task IneligibleIdentity_ReturnsNoCurrentOrHistoricalRows(string scenario)
    {
        await using var test = await TestContext.CreateAsync(Now);
        await test.SeedAsync([
            test.Reservation(101, TestContext.OwnerId, ReservationStatus.Active, Now.AddMinutes(-30), Now.AddMinutes(30)),
            test.Reservation(102, TestContext.OwnerId, ReservationStatus.Canceled, Now.AddDays(-1), Now.AddDays(-1).AddHours(1))
        ]);
        ClaimsPrincipal? principal = test.Owner;
        switch (scenario)
        {
            case "null principal": principal = null; break;
            case "anonymous": principal = Principal(TestContext.OwnerId, authenticated: false); break;
            case "missing identifier": principal = new ClaimsPrincipal(new ClaimsIdentity([], "Test")); break;
            case "blank identifier": principal = Principal(" "); break;
            case "unknown account": principal = Principal("nonexistent-overview-account"); break;
            case "inactive account":
                await test.Database.Users.Where(row => row.Id == TestContext.OwnerId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
                break;
            case "missing student membership":
                await test.Database.UserRoles.Where(row => row.UserId == TestContext.OwnerId).ExecuteDeleteAsync();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        var before = await test.SnapshotAsync();

        var overview = await test.Service.GetForUserAsync(principal);

        Assert.Equal(0, overview.ActiveCount);
        Assert.Empty(overview.Current);
        Assert.Empty(overview.RecentHistory);
        await test.AssertUnchangedAsync(before);
    }

    private static ClaimsPrincipal Principal(string id, bool authenticated = true)
        => new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, ApplicationRoles.Student)
        ], authenticated ? "Overview test" : null));

    private sealed class ControlledClock(DateTimeOffset now) : TimeProvider
    {
        public int ReadCount { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return now;
        }
    }

    private sealed class QueryRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public void Clear() => Commands.Clear();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record Snapshot(object[] Listings, object[] Reservations, object[] Audits);

    private sealed class TestContext(
        SqliteConnection connection, ApplicationDbContext database, ControlledClock clock, QueryRecorder commands)
        : IAsyncDisposable
    {
        public const string OwnerId = "overview-owner";
        public const string OtherOwnerId = "overview-other-owner";
        public ApplicationDbContext Database { get; } = database;
        public ControlledClock Clock { get; } = clock;
        public QueryRecorder Commands { get; } = commands;
        public ClaimsPrincipal Owner { get; } = Principal(OwnerId);
        public ReservationService Service { get; } = new(database,
            new PickupHoursEligibility(Options.Create(new ReservationOptions()), clock),
            NullLogger<ReservationService>.Instance, clock);

        public static async Task<TestContext> CreateAsync(DateTimeOffset now)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var commands = new QueryRecorder();
            var database = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).AddInterceptors(commands).Options);
            await database.Database.EnsureCreatedAsync();
            database.Roles.Add(new IdentityRole { Id = "overview-student-role", Name = ApplicationRoles.Student });
            foreach (var id in new[] { OwnerId, OtherOwnerId })
            {
                database.Users.Add(new ApplicationUser { Id = id, DisplayName = "Private student name", IsActive = true });
                database.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = "overview-student-role" });
            }
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            return new TestContext(connection, database, new ControlledClock(now), commands);
        }

        public Reservation Reservation(int id, string userId, ReservationStatus status,
            DateTimeOffset createdAt, DateTimeOffset expiresAt) => new()
        {
            Id = id, UserId = userId, Status = status, CreatedAtUtc = createdAt.UtcDateTime,
            ExpiresAtUtc = expiresAt.UtcDateTime,
            FulfilledAtUtc = status == ReservationStatus.Fulfilled ? createdAt.AddMinutes(1).UtcDateTime : null,
            CancellationReason = status == ReservationStatus.Canceled ? "Private cancellation history." : null,
            Listing = new Listing
            {
                Id = id + 1000, Title = $"Overview fixture item {id}", Description = "Overview test listing.",
                Condition = "Good", CategoryId = 1, CreatedAtUtc = Now.AddDays(-2).UtcDateTime,
                Status = status == ReservationStatus.Active ? ListingStatus.Held
                    : status == ReservationStatus.Fulfilled ? ListingStatus.Collected : ListingStatus.Available
            }
        };

        public async Task SeedAsync(Reservation[] reservations)
        {
            Database.Reservations.AddRange(reservations);
            Database.AuditEvents.Add(new AuditEvent
            {
                Listing = reservations[0].Listing, ActorUserId = OwnerId, Action = InventoryAuditActions.Reserved,
                OccurredAtUtc = reservations[0].CreatedAtUtc, Details = "Existing reservation audit history."
            });
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
        }

        public async Task<Snapshot> SnapshotAsync()
        {
            var listings = await Database.Listings.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
            var reservations = await Database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
            var audits = await Database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
            return new Snapshot(
                listings.Select(row => (object)(row.Id, row.Title, row.Description, row.Condition,
                    row.Size, row.CategoryId, row.Status, row.CreatedAtUtc)).ToArray(),
                reservations.Select(row => (object)(row.Id, row.ListingId, row.UserId, row.Status,
                    row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason)).ToArray(),
                audits.Select(row => (object)(row.Id, row.ListingId, row.ActorUserId, row.Action,
                    row.OccurredAtUtc, row.Details)).ToArray());
        }

        public async Task AssertUnchangedAsync(Snapshot before)
        {
            var after = await SnapshotAsync();
            Assert.Equal(before.Listings, after.Listings);
            Assert.Equal(before.Reservations, after.Reservations);
            Assert.Equal(before.Audits, after.Audits);
            Assert.Empty(Database.ChangeTracker.Entries());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
