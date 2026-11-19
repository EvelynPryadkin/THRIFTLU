using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

// Synthetic records in the existing isolated SQLite host; never a development database.
public sealed class ActivityHistoryServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("anonymous", ActivityHistoryQueryStatus.AuthenticationRequired)]
    [InlineData("missing-id", ActivityHistoryQueryStatus.AuthenticationRequired)]
    [InlineData("student", ActivityHistoryQueryStatus.AdministratorRequired)]
    [InlineData("staff", ActivityHistoryQueryStatus.AdministratorRequired)]
    [InlineData("inactive", ActivityHistoryQueryStatus.AdministratorRequired)]
    [InlineData("revoked", ActivityHistoryQueryStatus.AdministratorRequired)]
    [InlineData("deleted", ActivityHistoryQueryStatus.AdministratorRequired)]
    public async Task BothHistories_RequireCurrentActiveAdministratorInsteadOfRoleClaims(
        string state, ActivityHistoryQueryStatus expected)
    {
        await using var test = await Fixture.CreateAsync();
        await test.SeedOneOfEachAsync();
        ClaimsPrincipal? principal = test.Principal;
        if (state == "anonymous") principal = new ClaimsPrincipal(new ClaimsIdentity());
        else if (state == "missing-id") principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, ApplicationRoles.Administrator)], "Synthetic authentication"));
        else if (state is "student" or "staff")
        {
            Assert.True((await test.Users.RemoveFromRoleAsync(test.Admin, ApplicationRoles.Administrator)).Succeeded);
            Assert.True((await test.Users.AddToRoleAsync(test.Admin,
                state == "student" ? ApplicationRoles.Student : ApplicationRoles.Staff)).Succeeded);
        }
        else if (state == "inactive")
            await test.Database.Users.Where(row => row.Id == test.Admin.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        else if (state == "revoked")
            await test.Database.UserRoles.Where(row => row.UserId == test.Admin.Id).ExecuteDeleteAsync();
        else if (state == "deleted")
            await test.Database.Users.Where(row => row.Id == test.Admin.Id).ExecuteDeleteAsync();
        var before = await test.SnapshotAsync();

        foreach (var history in Enum.GetValues<ActivityHistoryKind>())
        {
            // The principal still claims Administrator after every database change.
            var result = await test.Service.GetPageAsync(new() { History = history }, principal);
            Assert.Equal(expected, result.Status);
            Assert.Null(result.Page);
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task SeparateHistories_ReturnOnlyDisplayFieldsAndControlledSummariesWithoutTrackingOrWrites()
    {
        await using var test = await Fixture.CreateAsync();
        await test.SeedOneOfEachAsync();
        const string hiddenPayload = "Password=synthetic-payload; token=synthetic-token; /private/synthetic/storage/photo.png";
        await test.Database.AuditEvents.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Details, hiddenPayload));
        var listingTitle = await test.Database.Listings.Where(row => row.Id == 1).Select(row => row.Title).SingleAsync();
        var before = await test.SnapshotAsync();
        test.Database.ChangeTracker.Clear();

        var inventory = await test.Service.GetPageAsync(new(), test.Principal);
        var administrative = await test.Service.GetPageAsync(new() { History = ActivityHistoryKind.Administration }, test.Principal);

        Assert.Equal(ActivityHistoryQueryStatus.Success, inventory.Status);
        Assert.Equal(ActivityHistoryQueryStatus.Success, administrative.Status);
        var item = Assert.Single(inventory.Page!.Items);
        Assert.Equal(1, item.ListingId);
        Assert.Equal(listingTitle, item.ListingTitle);
        Assert.Equal(test.Admin.Id, item.ActorId);
        Assert.Equal(test.Admin.DisplayName, item.ActorDisplayName);
        Assert.Equal(Now, item.OccurredAtUtc);
        Assert.Null(item.TargetUserId);
        Assert.Null(item.ScheduleId);
        Assert.NotEmpty(item.ActionLabel);
        Assert.NotEmpty(item.Summary);
        var admin = Assert.Single(administrative.Page!.Items);
        Assert.Null(admin.ListingId);
        Assert.Equal(1, admin.ScheduleId);
        Assert.Equal(Now, admin.OccurredAtUtc);
        Assert.DoesNotContain("synthetic-payload", JsonSerializer.Serialize(new[] { inventory, administrative }));
        Assert.DoesNotContain("synthetic-token", JsonSerializer.Serialize(new[] { inventory, administrative }));
        Assert.DoesNotContain("/private/synthetic", JsonSerializer.Serialize(new[] { inventory, administrative }));
        Assert.Empty(test.Database.ChangeTracker.Entries());
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task InventoryProjection_DoesNotFetchRawDetailsOrAccountSecretsAndPagesInSql()
    {
        await using var test = await Fixture.CreateAsync();
        await test.SeedOneOfEachAsync();
        var recorder = new QueryRecorder();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(test.Database.Database.GetDbConnection()).AddInterceptors(recorder).Options;
        await using var queryContext = new ApplicationDbContext(options);

        var result = await new ActivityHistoryService(queryContext).GetPageAsync(new(), test.Principal);

        Assert.Equal(ActivityHistoryQueryStatus.Success, result.Status);
        Assert.Contains(recorder.Statements, sql => sql.Contains("FROM \"AuditEvents\"", StringComparison.Ordinal)
            && sql.Contains("LIMIT", StringComparison.Ordinal) && sql.Contains("OFFSET", StringComparison.Ordinal));
        foreach (var sql in recorder.Statements)
        {
            foreach (var excludedColumn in new[] { "Details", "PasswordHash", "SecurityStamp", "Email", "StorageFileName" })
                Assert.DoesNotContain('"' + excludedColumn + '"', sql);
        }
        Assert.Empty(queryContext.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(ActivityHistoryKind.Inventory)]
    [InlineData(ActivityHistoryKind.Administration)]
    public async Task Pagination_OrdersByTimestampThenIdWithNoDuplicatesAndSafeBoundaries(ActivityHistoryKind history)
    {
        await using var test = await Fixture.CreateAsync();
        for (var index = 0; index < 45; index++)
        {
            if (history == ActivityHistoryKind.Inventory)
                test.Database.AuditEvents.Add(Inventory(test.Admin.Id, occurredAt: Now.AddMinutes(index % 3)));
            else
                test.Database.AdministrativeAuditEvents.Add(Administrative(test.Admin.Id, occurredAt: Now.AddMinutes(index % 3)));
        }
        await test.Database.SaveChangesAsync();
        var expected = history == ActivityHistoryKind.Inventory
            ? await test.Database.AuditEvents.OrderByDescending(row => row.OccurredAtUtc).ThenByDescending(row => row.Id).Select(row => row.Id).ToArrayAsync()
            : await test.Database.AdministrativeAuditEvents.OrderByDescending(row => row.OccurredAtUtc).ThenByDescending(row => row.Id).Select(row => row.Id).ToArrayAsync();
        var before = await test.SnapshotAsync();
        var received = new List<int>();
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var result = await test.Service.GetPageAsync(new() { History = history }, test.Principal, pageNumber);
            Assert.Equal(ActivityHistoryQueryStatus.Success, result.Status);
            Assert.Equal(45, result.Page!.TotalCount);
            Assert.Equal(3, result.Page.TotalPages);
            Assert.Equal(pageNumber, result.Page.PageNumber);
            Assert.Equal(pageNumber == 3 ? 5 : 20, result.Page.Items.Count);
            Assert.Equal(pageNumber > 1, result.Page.HasPreviousPage);
            Assert.Equal(pageNumber < 3, result.Page.HasNextPage);
            received.AddRange(result.Page.Items.Select(row => row.Id));
        }
        Assert.Equal(expected, received);
        Assert.Equal(45, received.Distinct().Count());
        foreach (var invalidPage in new[] { int.MinValue, 0, int.MaxValue })
        {
            var result = await test.Service.GetPageAsync(new() { History = history }, test.Principal, invalidPage);
            Assert.Equal(invalidPage == int.MaxValue ? 3 : 1, result.Page!.PageNumber);
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task InventoryFilters_CombineActionExactActorAndListingBeforeCountAndPagination()
    {
        await using var test = await Fixture.CreateAsync();
        const string exactActor = "historical-actor%_id";
        test.Database.AuditEvents.AddRange(
            Inventory(exactActor, 1, InventoryAuditActions.Updated),
            Inventory(exactActor, 2, InventoryAuditActions.Updated),
            Inventory(exactActor, 1, InventoryAuditActions.Published),
            Inventory("historical-actorABid", 1, InventoryAuditActions.Updated));
        await test.Database.SaveChangesAsync();
        var before = await test.SnapshotAsync();

        var result = await test.Service.GetPageAsync(new()
        {
            ActorId = "  " + exactActor + "  ", ListingId = 1, Action = "  " + InventoryAuditActions.Updated + "  "
        }, test.Principal, int.MaxValue);

        Assert.Equal(ActivityHistoryQueryStatus.Success, result.Status);
        Assert.Equal(1, result.Page!.TotalCount);
        var record = Assert.Single(result.Page.Items);
        Assert.Equal(exactActor, record.ActorId);
        Assert.Equal(1, record.ListingId);
        Assert.Equal(1, result.Page.PageNumber);
        Assert.Null(record.ActorDisplayName);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task AdministrativeFilters_CombineExactTargetActorActionAndScheduleWithoutWildcardMatching()
    {
        await using var test = await Fixture.CreateAsync();
        const string target = "historical-target%_id";
        var exact = new AdministrativeAuditEvent
        {
            ActorUserId = test.Admin.Id, TargetUserId = target, Action = AdministrativeAuditActions.StaffGranted, OccurredAtUtc = Now
        };
        test.Database.AdministrativeAuditEvents.AddRange(exact,
            new AdministrativeAuditEvent { ActorUserId = test.Admin.Id, TargetUserId = "historical-targetABid", Action = AdministrativeAuditActions.StaffGranted, OccurredAtUtc = Now },
            new AdministrativeAuditEvent { ActorUserId = "other-actor", TargetUserId = target, Action = AdministrativeAuditActions.StaffGranted, OccurredAtUtc = Now },
            new AdministrativeAuditEvent { ActorUserId = test.Admin.Id, TargetUserId = target, Action = AdministrativeAuditActions.StaffRemoved, OccurredAtUtc = Now },
            Administrative(test.Admin.Id), Administrative(test.Admin.Id, scheduleId: 2));
        await test.Database.SaveChangesAsync();
        var before = await test.SnapshotAsync();

        var account = await test.Service.GetPageAsync(new()
        {
            History = ActivityHistoryKind.Administration, ActorId = test.Admin.Id,
            TargetUserId = " " + target + " ", Action = AdministrativeAuditActions.StaffGranted
        }, test.Principal);
        Assert.Equal(ActivityHistoryQueryStatus.Success, account.Status);
        Assert.Equal(1, account.Page!.TotalCount);
        var accountRow = Assert.Single(account.Page.Items);
        Assert.Equal(exact.Id, accountRow.Id);
        Assert.Equal(target, accountRow.TargetUserId);
        Assert.Null(accountRow.TargetDisplayName);

        var schedule = await test.Service.GetPageAsync(new()
        {
            History = ActivityHistoryKind.Administration, ActorId = test.Admin.Id,
            ScheduleId = 2, Action = AdministrativeAuditActions.PickupScheduleSaved
        }, test.Principal);
        Assert.Equal(ActivityHistoryQueryStatus.Success, schedule.Status);
        Assert.Equal(1, schedule.Page!.TotalCount);
        Assert.Equal(2, Assert.Single(schedule.Page.Items).ScheduleId);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("history")]
    [InlineData("long-action")]
    [InlineData("unknown-action")]
    [InlineData("wrong-action-family")]
    [InlineData("long-actor")]
    [InlineData("actor-control")]
    [InlineData("zero-listing")]
    [InlineData("long-target")]
    [InlineData("target-control")]
    [InlineData("zero-schedule")]
    [InlineData("inventory-target")]
    [InlineData("inventory-schedule")]
    [InlineData("admin-listing")]
    public async Task InvalidFilters_ReturnValidationWithoutReadingRecordsOrWriting(string scenario)
    {
        await using var test = await Fixture.CreateAsync();
        await test.SeedOneOfEachAsync();
        ActivityHistoryQueryInput? input = new();
        switch (scenario)
        {
            case "null": input = null; break;
            case "history": input.History = (ActivityHistoryKind)99; break;
            case "long-action": input.Action = new string('x', 51); break;
            case "unknown-action": input.Action = "DeleteAudit"; break;
            case "wrong-action-family": input.Action = AdministrativeAuditActions.StaffGranted; break;
            case "long-actor": input.ActorId = new string('x', 451); break;
            case "actor-control": input.ActorId = "test\0actor"; break;
            case "zero-listing": input.ListingId = 0; break;
            case "long-target": input.History = ActivityHistoryKind.Administration; input.TargetUserId = new string('x', 451); break;
            case "target-control": input.History = ActivityHistoryKind.Administration; input.TargetUserId = "test\0target"; break;
            case "zero-schedule": input.History = ActivityHistoryKind.Administration; input.ScheduleId = 0; break;
            case "inventory-target": input.TargetUserId = "target"; break;
            case "inventory-schedule": input.ScheduleId = 1; break;
            case "admin-listing": input.History = ActivityHistoryKind.Administration; input.ListingId = 1; break;
        }
        var before = await test.SnapshotAsync();
        var result = await test.Service.GetPageAsync(input, test.Principal);
        Assert.Equal(ActivityHistoryQueryStatus.InvalidInput, result.Status);
        Assert.Null(result.Page);
        Assert.NotEmpty(result.Message);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ActivityHistoryKind.Inventory)]
    [InlineData(ActivityHistoryKind.Administration)]
    public async Task UnknownAction_UsesSafeFallbackInsteadOfLeakingStoredPayload(ActivityHistoryKind history)
    {
        await using var test = await Fixture.CreateAsync();
        const string unsafeAction = "Token=synthetic-sensitive-payload";
        if (history == ActivityHistoryKind.Inventory)
        {
            var audit = Inventory(test.Admin.Id, action: unsafeAction);
            audit.Details = "Host=synthetic;Password=synthetic; /private/fixture/photo.png";
            test.Database.AuditEvents.Add(audit);
        }
        else
        {
            var audit = Administrative(test.Admin.Id);
            audit.Action = unsafeAction;
            test.Database.AdministrativeAuditEvents.Add(audit);
        }
        await test.Database.SaveChangesAsync();

        var result = await test.Service.GetPageAsync(new() { History = history }, test.Principal);

        Assert.Equal(ActivityHistoryQueryStatus.Success, result.Status);
        var record = Assert.Single(result.Page!.Items);
        Assert.Equal("Unrecognized action", record.ActionLabel);
        Assert.DoesNotContain("synthetic-sensitive-payload", JsonSerializer.Serialize(record));
        Assert.DoesNotContain("Host=", JsonSerializer.Serialize(record));
        Assert.DoesNotContain("Password=", JsonSerializer.Serialize(record));
        Assert.DoesNotContain("/private/", JsonSerializer.Serialize(record));
    }

    [Fact]
    public async Task DeletedAccount_PreservesHistoricalActorAndTargetIdentifiers()
    {
        await using var test = await Fixture.CreateAsync();
        var email = IdentityTestContext.NewEmail();
        var departed = new ApplicationUser { DisplayName = "Synthetic former account", Email = email, UserName = email };
        Assert.True((await test.Users.CreateAsync(departed)).Succeeded);
        test.Database.AuditEvents.Add(Inventory(departed.Id));
        test.Database.AdministrativeAuditEvents.Add(new AdministrativeAuditEvent
        {
            ActorUserId = departed.Id, TargetUserId = departed.Id,
            Action = AdministrativeAuditActions.StaffRemoved, OccurredAtUtc = Now
        });
        await test.Database.SaveChangesAsync();
        Assert.True((await test.Users.DeleteAsync(departed)).Succeeded);
        var before = await test.SnapshotAsync();

        foreach (var history in Enum.GetValues<ActivityHistoryKind>())
        {
            var result = await test.Service.GetPageAsync(new() { History = history }, test.Principal);
            Assert.Equal(ActivityHistoryQueryStatus.Success, result.Status);
            var record = Assert.Single(result.Page!.Items);
            Assert.Equal(departed.Id, record.ActorId);
            Assert.Null(record.ActorDisplayName);
            if (history == ActivityHistoryKind.Administration)
            {
                Assert.Equal(departed.Id, record.TargetUserId);
                Assert.Null(record.TargetDisplayName);
            }
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(ActivityHistoryKind.Inventory)]
    [InlineData(ActivityHistoryKind.Administration)]
    public async Task EmptyOrUnmatchedHistory_ReturnsPageOneWithoutWrites(ActivityHistoryKind history)
    {
        await using var test = await Fixture.CreateAsync();
        foreach (var actorId in new string?[] { null, "missing-historical-actor" })
        {
            var before = await test.SnapshotAsync();
            var result = await test.Service.GetPageAsync(new() { History = history, ActorId = actorId }, test.Principal, int.MaxValue);
            Assert.Equal(ActivityHistoryQueryStatus.Success, result.Status);
            Assert.Empty(result.Page!.Items);
            Assert.Equal(0, result.Page.TotalCount);
            Assert.Equal(1, result.Page.PageNumber);
            Assert.Equal(1, result.Page.TotalPages);
            Assert.False(result.Page.HasPreviousPage);
            Assert.False(result.Page.HasNextPage);
            Assert.Equal(before, await test.SnapshotAsync());
        }
    }

    private static AuditEvent Inventory(string actor, int listingId = 1, string action = InventoryAuditActions.Updated, DateTime? occurredAt = null)
        => new() { ActorUserId = actor, ListingId = listingId, Action = action, OccurredAtUtc = occurredAt ?? Now };
    private static AdministrativeAuditEvent Administrative(string actor, int scheduleId = 1, DateTime? occurredAt = null)
        => new() { ActorUserId = actor, TargetScheduleId = scheduleId, Action = AdministrativeAuditActions.PickupScheduleSaved, OccurredAtUtc = occurredAt ?? Now };
    private static ClaimsPrincipal PrincipalFor(string id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, ApplicationRoles.Administrator)], "Synthetic authentication"));

    private sealed class QueryRecorder : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Statements.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture(AuthorizationWebApplicationFactory factory, AsyncServiceScope scope,
        ApplicationDbContext database, UserManager<ApplicationUser> users, ApplicationUser admin) : IAsyncDisposable
    {
        public ApplicationDbContext Database { get; } = database;
        public UserManager<ApplicationUser> Users { get; } = users;
        public ApplicationUser Admin { get; } = admin;
        public ClaimsPrincipal Principal { get; } = PrincipalFor(admin.Id);
        public ActivityHistoryService Service { get; } = new(database);
        public static async Task<Fixture> CreateAsync()
        {
            var factory = new AuthorizationWebApplicationFactory();
            var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = IdentityTestContext.NewEmail();
            var admin = new ApplicationUser { DisplayName = "Synthetic activity administrator", Email = email, UserName = email };
            Assert.True((await users.CreateAsync(admin)).Succeeded);
            Assert.True((await users.AddToRoleAsync(admin, ApplicationRoles.Administrator)).Succeeded);
            return new(factory, scope, database, users, admin);
        }
        public async Task SeedOneOfEachAsync()
        {
            Database.AuditEvents.Add(Inventory(Admin.Id));
            Database.AdministrativeAuditEvents.Add(Administrative(Admin.Id));
            await Database.SaveChangesAsync();
        }
        public async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
        {
            Audits = await Database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                { row.Id, row.ActorUserId, row.Action, row.ListingId, row.OccurredAtUtc, row.Details }).ToArrayAsync(),
            Administrative = await Database.AdministrativeAuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Listings = await Database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Status, row.Title }).ToArrayAsync(),
            Reservations = await Database.Reservations.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                { row.Id, row.ListingId, row.UserId, row.Status, row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason }).ToArrayAsync(),
            Accounts = await Database.Users.AsNoTracking().OrderBy(row => row.Id).Select(row => new
                { row.Id, row.DisplayName, row.IsActive, row.SecurityStamp, row.ConcurrencyStamp }).ToArrayAsync(),
            Roles = await Database.UserRoles.AsNoTracking().OrderBy(row => row.UserId).ThenBy(row => row.RoleId).ToArrayAsync()
        });
        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            factory.Dispose();
        }
    }
}
