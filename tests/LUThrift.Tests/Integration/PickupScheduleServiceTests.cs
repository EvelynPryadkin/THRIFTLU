using System.Security.Claims;
using System.Text.Json;
using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

// All hours and accounts in these tests are synthetic, not an official campus schedule.
public sealed class PickupScheduleServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UnconfiguredRead_DoesNotCreateScheduleOrAuditAndRemainsUnavailable()
    {
        await using var test = await TestContext.CreateAsync();
        var before = await test.SnapshotAsync();

        var snapshot = await test.Service.GetAsync();
        var again = await test.Service.GetAsync();

        Assert.Null(snapshot.Version);
        Assert.False(snapshot.IsConfigured);
        Assert.Empty(snapshot.WeeklyPickupIntervals);
        Assert.Empty(snapshot.ClosedDates);
        Assert.Null(again.Version);
        Assert.Empty(await test.Database.PickupSchedules.ToListAsync());
        Assert.Equal(PickupHoursStatus.ScheduleUnavailable, Eligibility(snapshot.CampusTimeZone,
            snapshot.WeeklyPickupIntervals, snapshot.ClosedDates).Evaluate().Status);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Save_PersistsCanonicalScheduleAcrossScopesWithActorAndServerTimeAudit()
    {
        await using var test = await TestContext.CreateAsync();
        var input = ValidInput();
        input.CampusTimeZone = " America/Chicago ";
        input.WeeklyPickupIntervals.Reverse();
        input.ClosedDates = [" 2026-01-07 ", "2026-01-06", "2026-01-06"];

        var result = await test.Service.SaveAsync(input, test.Actor);

        Assert.Equal(PickupScheduleOperationStatus.Saved, result.Status);
        Assert.NotNull(result.Version);
        Assert.NotEqual(Guid.Empty, result.Version);
        await using var verification = test.Factory.Services.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var loaded = await Service(database).GetAsync();
        Assert.Equal(result.Version, loaded.Version);
        Assert.Equal("America/Chicago", loaded.CampusTimeZone);
        Assert.True(loaded.IsConfigured);
        Assert.Equal(new[] { "09:00", "13:00" }, loaded.WeeklyPickupIntervals.Select(interval => interval.OpensAt));
        Assert.All(loaded.WeeklyPickupIntervals, interval => Assert.Equal("Monday", interval.Day));
        Assert.Equal(new[] { "2026-01-06", "2026-01-07" }, loaded.ClosedDates);
        var audit = await database.AdministrativeAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(test.Admin.Id, audit.ActorUserId);
        Assert.Null(audit.TargetUserId);
        Assert.Equal(PickupSchedule.SingletonId, audit.TargetScheduleId);
        Assert.Equal(AdministrativeAuditActions.PickupScheduleSaved, audit.Action);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAtUtc);
        Assert.Empty(await database.AuditEvents.ToListAsync());
        Assert.Equal(PickupHoursStatus.Eligible, Eligibility(loaded.CampusTimeZone,
            loaded.WeeklyPickupIntervals, loaded.ClosedDates).Evaluate().Status);
    }

    [Fact]
    public async Task Save_WithCurrentVersionReplacesIntervalsAndCanExplicitlyDisableSchedule()
    {
        await using var test = await TestContext.CreateAsync();
        var first = await test.Service.SaveAsync(ValidInput(), test.Actor);
        var revised = ValidInput(first.Version);
        revised.WeeklyPickupIntervals = [Interval("Tuesday", "10:00", "14:00")];
        revised.ClosedDates = ["2026-01-08"];

        var changed = await test.Service.SaveAsync(revised, test.Actor);

        Assert.Equal(PickupScheduleOperationStatus.Saved, changed.Status);
        Assert.NotEqual(first.Version, changed.Version);
        var loaded = await test.Service.GetAsync();
        Assert.Equal("Tuesday", Assert.Single(loaded.WeeklyPickupIntervals).Day);
        Assert.Equal("2026-01-08", Assert.Single(loaded.ClosedDates));
        var disabled = await test.Service.SaveAsync(new UpdatePickupScheduleInput
        {
            ExpectedVersion = changed.Version, CampusTimeZone = "America/Chicago"
        }, test.Actor);
        Assert.Equal(PickupScheduleOperationStatus.Saved, disabled.Status);
        loaded = await test.Service.GetAsync();
        Assert.NotNull(loaded.Version);
        Assert.False(loaded.IsConfigured);
        Assert.Empty(loaded.WeeklyPickupIntervals);
        Assert.Empty(loaded.ClosedDates);
        Assert.Equal(PickupHoursStatus.ScheduleUnavailable, Eligibility(loaded.CampusTimeZone,
            loaded.WeeklyPickupIntervals, loaded.ClosedDates).Evaluate().Status);
        Assert.Equal(3, await test.Database.AdministrativeAuditEvents.CountAsync());
    }

    [Fact]
    public async Task EquivalentNormalizedSave_IsNoOpWithoutVersionOrAuditChange()
    {
        await using var test = await TestContext.CreateAsync();
        var first = await test.Service.SaveAsync(ValidInput(), test.Actor);
        var before = await test.SnapshotAsync();
        var same = ValidInput(first.Version);
        same.WeeklyPickupIntervals.Reverse();
        same.WeeklyPickupIntervals[0].Day = " monday ";
        same.CampusTimeZone = " America/Chicago ";
        same.ClosedDates.Add(same.ClosedDates[0]);

        var result = await test.Service.SaveAsync(same, test.Actor);

        Assert.Equal(PickupScheduleOperationStatus.Unchanged, result.Status);
        Assert.Equal(first.Version, result.Version);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task StaleEditorsAndStaleFirstSave_CannotOverwriteWinner()
    {
        await using var test = await TestContext.CreateAsync();
        await using var otherScope = test.Factory.Services.CreateAsyncScope();
        var other = Service(otherScope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        var firstEditor = await test.Service.GetAsync();
        var secondEditor = await other.GetAsync();
        Assert.Null(firstEditor.Version);
        Assert.Null(secondEditor.Version);
        var created = await test.Service.SaveAsync(ValidInput(firstEditor.Version), test.Actor);
        var afterFirstSave = await test.SnapshotAsync();
        var firstConflict = await other.SaveAsync(ValidInput(secondEditor.Version), test.Actor);
        Assert.Equal(PickupScheduleOperationStatus.Conflict, firstConflict.Status);
        Assert.Equal(afterFirstSave, await test.SnapshotAsync());

        var stale = await other.GetAsync();
        var winner = ValidInput(created.Version);
        winner.ClosedDates = ["2026-02-01"];
        var updated = await test.Service.SaveAsync(winner, test.Actor);
        Assert.Equal(PickupScheduleOperationStatus.Saved, updated.Status);
        var beforeRejectedEdit = await test.SnapshotAsync();
        var losingEdit = ValidInput(stale.Version);
        losingEdit.WeeklyPickupIntervals.Clear();
        var rejected = await other.SaveAsync(losingEdit, test.Actor);
        Assert.Equal(PickupScheduleOperationStatus.Conflict, rejected.Status);
        Assert.Equal(beforeRejectedEdit, await test.SnapshotAsync());
        // Even an identical payload from an old edit must not silently replace the version.
        Assert.Equal(PickupScheduleOperationStatus.Conflict,
            (await other.SaveAsync(winner, test.Actor)).Status);
    }

    [Fact]
    public async Task UnknownVersion_CannotInitializeSchedule()
    {
        await using var test = await TestContext.CreateAsync();
        var before = await test.SnapshotAsync();
        var result = await test.Service.SaveAsync(ValidInput(Guid.NewGuid()), test.Actor);
        Assert.Equal(PickupScheduleOperationStatus.Conflict, result.Status);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("anonymous", PickupScheduleOperationStatus.AuthenticationRequired)]
    [InlineData("student", PickupScheduleOperationStatus.AdministratorRequired)]
    [InlineData("staff", PickupScheduleOperationStatus.AdministratorRequired)]
    [InlineData("revoked", PickupScheduleOperationStatus.AdministratorRequired)]
    [InlineData("inactive", PickupScheduleOperationStatus.AdministratorRequired)]
    [InlineData("deleted", PickupScheduleOperationStatus.AdministratorRequired)]
    public async Task SaveAndImport_RequireCurrentActiveAdministrator(string state, PickupScheduleOperationStatus expected)
    {
        await using var test = await TestContext.CreateAsync();
        ClaimsPrincipal? actor = test.Actor;
        if (state == "anonymous") actor = new ClaimsPrincipal(new ClaimsIdentity());
        else if (state is "student" or "staff")
        {
            Assert.True((await test.Users.RemoveFromRoleAsync(test.Admin, ApplicationRoles.Administrator)).Succeeded);
            Assert.True((await test.Users.AddToRoleAsync(test.Admin,
                state == "student" ? ApplicationRoles.Student : ApplicationRoles.Staff)).Succeeded);
        }
        else if (state == "revoked")
            await test.Database.UserRoles.Where(row => row.UserId == test.Admin.Id).ExecuteDeleteAsync();
        else if (state == "inactive")
            await test.Database.Users.Where(row => row.Id == test.Admin.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        else if (state == "deleted")
            await test.Database.Users.Where(row => row.Id == test.Admin.Id).ExecuteDeleteAsync();
        var before = await test.SnapshotAsync();

        var saved = await test.Service.SaveAsync(ValidInput(), actor);
        var imported = await test.Service.ImportConfiguredAsync(ConfiguredOptions(), actor);

        Assert.Equal(expected, saved.Status);
        Assert.Equal(expected, imported.Status);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("timezone")]
    [InlineData("overlap")]
    [InlineData("touching")]
    [InlineData("overnight")]
    [InlineData("time")]
    [InlineData("numeric-day")]
    [InlineData("date")]
    [InlineData("too-many-intervals")]
    [InlineData("too-many-dates")]
    public async Task InvalidSchedule_DoesNotChangePersistedScheduleOrAudit(string invalid)
    {
        await using var test = await TestContext.CreateAsync();
        var saved = await test.Service.SaveAsync(ValidInput(), test.Actor);
        UpdatePickupScheduleInput? input = ValidInput(saved.Version);
        switch (invalid)
        {
            case "null": input = null; break;
            case "timezone": input.CampusTimeZone = "Not/AZone"; break;
            case "overlap": input.WeeklyPickupIntervals.Add(Interval("Monday", "11:00", "14:00")); break;
            case "touching": input.WeeklyPickupIntervals.Add(Interval("Monday", "12:00", "13:00")); break;
            case "overnight": input.WeeklyPickupIntervals[0] = Interval("Monday", "22:00", "02:00"); break;
            case "time": input.WeeklyPickupIntervals[0].OpensAt = "9:00"; break;
            case "numeric-day": input.WeeklyPickupIntervals[0].Day = "1"; break;
            case "date": input.ClosedDates = ["2026-02-30"]; break;
            case "too-many-intervals":
                input.WeeklyPickupIntervals = Enumerable.Range(0, 43).Select(index =>
                    Interval(((DayOfWeek)(index / 7)).ToString(), $"{index % 7 * 3:00}:00", $"{index % 7 * 3 + 1:00}:00")).ToList();
                break;
            case "too-many-dates":
                input.ClosedDates = Enumerable.Range(0, 367).Select(offset => new DateOnly(2026, 1, 1).AddDays(offset).ToString("yyyy-MM-dd")).ToList();
                break;
        }
        var before = await test.SnapshotAsync();

        var result = await test.Service.SaveAsync(input, test.Actor);

        Assert.Equal(PickupScheduleOperationStatus.InvalidInput, result.Status);
        Assert.NotEmpty(result.Message);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditFailure_RollsBackFirstSaveOrReplacementIncludingVersion(bool existing)
    {
        await using var test = await TestContext.CreateAsync();
        Guid? version = null;
        if (existing) version = (await test.Service.SaveAsync(ValidInput(), test.Actor)).Version;
        var before = await test.SnapshotAsync();
        await test.Database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectScheduleAudit BEFORE INSERT ON "AdministrativeAuditEvents"
            BEGIN SELECT RAISE(ABORT, 'Synthetic schedule audit failure'); END;
            """);
        var input = ValidInput(version);
        input.ClosedDates = ["2026-02-03"];

        var result = await test.Service.SaveAsync(input, test.Actor);

        Assert.Equal(PickupScheduleOperationStatus.PersistenceFailed, result.Status);
        Assert.Equal(before, await test.SnapshotAsync());
        await test.Database.SaveChangesAsync();
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatabaseReadFailure_ReturnsSafeResultWithoutWriting(bool failAuthorization)
    {
        await using var test = await TestContext.CreateAsync();
        var before = await test.SnapshotAsync();
        // Temporarily hide one table in this isolated SQLite fixture to fail a
        // SELECT before any write. SQLite triggers cannot reject SELECT queries.
        var hideTable = failAuthorization
            ? "ALTER TABLE \"AspNetUserRoles\" RENAME TO \"UnavailableUserRoles\";"
            : "ALTER TABLE \"PickupSchedules\" RENAME TO \"UnavailablePickupSchedules\";";
        var restoreTable = failAuthorization
            ? "ALTER TABLE \"UnavailableUserRoles\" RENAME TO \"AspNetUserRoles\";"
            : "ALTER TABLE \"UnavailablePickupSchedules\" RENAME TO \"PickupSchedules\";";
        await test.Database.Database.ExecuteSqlRawAsync(hideTable);
        try
        {
            var saved = await test.Service.SaveAsync(ValidInput(), test.Actor);
            var imported = await test.Service.ImportConfiguredAsync(ConfiguredOptions(), test.Actor);

            foreach (var result in new[] { saved, imported })
            {
                Assert.Equal(PickupScheduleOperationStatus.PersistenceFailed, result.Status);
                Assert.Null(result.Version);
                Assert.Equal("Pickup hours could not be saved. Refresh before trying again.", result.Message);
            }
        }
        finally
        {
            await test.Database.Database.ExecuteSqlRawAsync(restoreTable);
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task ExplicitImport_CopiesOnlySelectedConfiguredScheduleAndRefusesOverwrite()
    {
        await using var test = await TestContext.CreateAsync();
        var configured = ConfiguredOptions();
        configured.ExpirationBatchSize = 17;
        configured.ExpirationPollSeconds = 7;

        var result = await test.Service.ImportConfiguredAsync(configured, test.Actor);

        Assert.Equal(PickupScheduleOperationStatus.Saved, result.Status);
        var loaded = await test.Service.GetAsync();
        Assert.Equal("America/Chicago", loaded.CampusTimeZone);
        Assert.Equal(new[] { "09:00", "13:00" }, loaded.WeeklyPickupIntervals.Select(interval => interval.OpensAt));
        var audit = await test.Database.AdministrativeAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AdministrativeAuditActions.PickupScheduleImported, audit.Action);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAtUtc);
        Assert.Equal(test.Admin.Id, audit.ActorUserId);
        Assert.Null(audit.TargetUserId);
        Assert.Equal(PickupSchedule.SingletonId, audit.TargetScheduleId);
        var beforeRepeat = await test.SnapshotAsync();
        configured.WeeklyPickupIntervals.Clear();
        Assert.Equal(PickupScheduleOperationStatus.Conflict,
            (await test.Service.ImportConfiguredAsync(configured, test.Actor)).Status);
        Assert.Equal(beforeRepeat, await test.SnapshotAsync());
        Assert.Equal(TimeSpan.FromHours(1), ReservationPolicy.Duration);
        Assert.Equal(3, ReservationPolicy.MaximumActiveReservations);
    }

    [Fact]
    public async Task ExplicitImport_DoesNotReplacePreviouslySavedEmptySchedule()
    {
        await using var test = await TestContext.CreateAsync();
        var empty = await test.Service.SaveAsync(new UpdatePickupScheduleInput(), test.Actor);
        Assert.Equal(PickupScheduleOperationStatus.Saved, empty.Status);
        var before = await test.SnapshotAsync();
        Assert.Equal(PickupScheduleOperationStatus.Conflict,
            (await test.Service.ImportConfiguredAsync(ConfiguredOptions(), test.Actor)).Status);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    private static UpdatePickupScheduleInput ValidInput(Guid? version = null) => new()
    {
        ExpectedVersion = version, CampusTimeZone = "America/Chicago",
        WeeklyPickupIntervals = [Interval("Monday", "09:00", "12:00"), Interval("Monday", "13:00", "16:00")],
        ClosedDates = ["2026-01-06"]
    };

    private static ReservationOptions ConfiguredOptions()
    {
        var input = ValidInput();
        return new() { CampusTimeZone = input.CampusTimeZone,
            WeeklyPickupIntervals = input.WeeklyPickupIntervals, ClosedDates = input.ClosedDates };
    }

    private static WeeklyPickupInterval Interval(string day, string opens, string closes)
        => new() { Day = day, OpensAt = opens, ClosesAt = closes };
    private static ClaimsPrincipal Principal(string id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Role, ApplicationRoles.Administrator)], "Schedule test"));
    private static PickupScheduleService Service(ApplicationDbContext database)
        => new(database, new Clock(), NullLogger<PickupScheduleService>.Instance);
    private static PickupHoursEligibility Eligibility(string zone, IReadOnlyList<WeeklyPickupInterval> intervals, IReadOnlyList<string> dates)
        => new(Options.Create(new ReservationOptions { CampusTimeZone = zone,
            WeeklyPickupIntervals = intervals.ToList(), ClosedDates = dates.ToList() }), new Clock());
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestContext(AuthorizationWebApplicationFactory factory, AsyncServiceScope scope,
        ApplicationDbContext database, UserManager<ApplicationUser> users, ApplicationUser admin) : IAsyncDisposable
    {
        public AuthorizationWebApplicationFactory Factory { get; } = factory;
        public ApplicationDbContext Database { get; } = database;
        public UserManager<ApplicationUser> Users { get; } = users;
        public ApplicationUser Admin { get; } = admin;
        public ClaimsPrincipal Actor { get; } = Principal(admin.Id);
        public PickupScheduleService Service { get; } = PickupScheduleServiceTests.Service(database);

        public static async Task<TestContext> CreateAsync()
        {
            var factory = new AuthorizationWebApplicationFactory();
            var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewEmail();
            var admin = new ApplicationUser { DisplayName = "Synthetic schedule administrator", Email = email, UserName = email };
            Assert.True((await users.CreateAsync(admin)).Succeeded);
            Assert.True((await users.AddToRoleAsync(admin, ApplicationRoles.Administrator)).Succeeded);
            return new(factory, scope, database, users, admin);
        }

        public async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
        {
            Schedule = await Service.GetAsync(),
            Audit = await Database.AdministrativeAuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Accounts = await Database.Users.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.IsActive, row.SecurityStamp, row.ConcurrencyStamp }).ToArrayAsync(),
            Roles = await Database.UserRoles.AsNoTracking().OrderBy(row => row.UserId).ThenBy(row => row.RoleId).ToArrayAsync(),
            Inventory = await Database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Status }).ToArrayAsync(),
            InventoryAudits = await Database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
        });

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            Factory.Dispose();
        }
    }
}
