using System.Security.Claims;
using System.Text.Json;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LUThrift.Tests.Integration;

public sealed class StaffAccessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 18, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(StaffAccessAction.Grant)]
    [InlineData(StaffAccessAction.Remove)]
    public async Task Change_UsesFixedStaffRolePreservesStudentAndAuditsOnceWithServerTime(StaffAccessAction action)
    {
        await using var test = await TestContext.CreateAsync(targetStaff: action == StaffAccessAction.Remove);
        var beforeStamp = await test.TargetStampAsync();

        var changed = await test.Service.ChangeStaffAsync(test.Target.Id, action, test.Actor);

        Assert.Equal(StaffAccessOperationStatus.Changed, changed.Status);
        var roles = await test.TargetRolesAsync();
        Assert.Contains(ApplicationRoles.Student, roles);
        Assert.Equal(action == StaffAccessAction.Grant, roles.Contains(ApplicationRoles.Staff));
        Assert.DoesNotContain(ApplicationRoles.Administrator, roles);
        var stamp = await test.TargetStampAsync();
        Assert.NotEqual(beforeStamp, stamp);
        var audit = await test.Database.AdministrativeAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(test.Admin.Id, audit.ActorUserId);
        Assert.Equal(test.Target.Id, audit.TargetUserId);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAtUtc);
        Assert.Equal(action == StaffAccessAction.Grant ? AdministrativeAuditActions.StaffGranted : AdministrativeAuditActions.StaffRemoved,
            audit.Action);
        var beforeRepeat = await test.SnapshotAsync();

        var repeated = await test.Service.ChangeStaffAsync(test.Target.Id, action, test.Actor);

        Assert.Equal(StaffAccessOperationStatus.Unchanged, repeated.Status);
        Assert.Equal(beforeRepeat, await test.SnapshotAsync());
        Assert.Equal(stamp, await test.TargetStampAsync());
    }

    [Theory]
    [InlineData("anonymous", StaffAccessQueryStatus.AuthenticationRequired, StaffAccessOperationStatus.AuthenticationRequired)]
    [InlineData("student", StaffAccessQueryStatus.AdministratorRequired, StaffAccessOperationStatus.AdministratorRequired)]
    [InlineData("staff", StaffAccessQueryStatus.AdministratorRequired, StaffAccessOperationStatus.AdministratorRequired)]
    [InlineData("inactive", StaffAccessQueryStatus.AdministratorRequired, StaffAccessOperationStatus.AdministratorRequired)]
    [InlineData("revoked", StaffAccessQueryStatus.AdministratorRequired, StaffAccessOperationStatus.AdministratorRequired)]
    [InlineData("deleted", StaffAccessQueryStatus.AdministratorRequired, StaffAccessOperationStatus.AdministratorRequired)]
    public async Task QueriesAndChanges_RecheckCurrentAdministratorRatherThanRoleClaims(string state,
        StaffAccessQueryStatus queryStatus, StaffAccessOperationStatus operationStatus)
    {
        await using var test = await TestContext.CreateAsync();
        ClaimsPrincipal? actor = test.Actor;
        if (state == "anonymous") actor = new ClaimsPrincipal(new ClaimsIdentity());
        else if (state is "student" or "staff")
        {
            if (state == "staff") Assert.True((await test.Users.AddToRoleAsync(test.Target, ApplicationRoles.Staff)).Succeeded);
            actor = Principal(test.Target.Id); // Deliberately includes a forged Administrator claim.
        }
        else if (state == "inactive")
            await test.Database.Users.Where(row => row.Id == test.Admin.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        else if (state == "revoked")
            await test.Database.UserRoles.Where(row => row.UserId == test.Admin.Id).ExecuteDeleteAsync();
        else if (state == "deleted")
            await test.Database.Users.Where(row => row.Id == test.Admin.Id).ExecuteDeleteAsync();
        var before = await test.SnapshotAsync();

        var query = await test.Service.GetPageAsync(new(), actor);
        var change = await test.Service.ChangeStaffAsync(test.Target.Id, StaffAccessAction.Grant, actor);

        Assert.Equal(queryStatus, query.Status);
        Assert.Null(query.Page);
        Assert.Equal(operationStatus, change.Status);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(StaffAccessAction.Grant)]
    [InlineData(StaffAccessAction.Remove)]
    public async Task AdministratorTarget_CannotBeModifiedIncludingSelf(StaffAccessAction action)
    {
        await using var test = await TestContext.CreateAsync();
        Assert.True((await test.Users.AddToRoleAsync(test.Target, ApplicationRoles.Administrator)).Succeeded);
        Assert.True((await test.Users.AddToRoleAsync(test.Target, ApplicationRoles.Staff)).Succeeded);
        var before = await test.SnapshotAsync();

        foreach (var target in new[] { test.Admin.Id, test.Target.Id })
        {
            var result = await test.Service.ChangeStaffAsync(target, action, test.Actor);
            Assert.Equal(StaffAccessOperationStatus.ProtectedAccount, result.Status);
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task UndefinedAction_IsRejectedWithoutWrites(int action)
    {
        await using var test = await TestContext.CreateAsync();
        var before = await test.SnapshotAsync();
        var result = await test.Service.ChangeStaffAsync(test.Target.Id, (StaffAccessAction)action, test.Actor);
        Assert.Equal(StaffAccessOperationStatus.InvalidInput, result.Status);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task InvalidOrUnknownTarget_IsSafeAndDoesNotWrite()
    {
        await using var test = await TestContext.CreateAsync();
        var before = await test.SnapshotAsync();
        foreach (var id in new string?[] { null, "", " ", new string('x', 451) })
        {
            var result = await test.Service.ChangeStaffAsync(id, StaffAccessAction.Grant, test.Actor);
            Assert.Equal(StaffAccessOperationStatus.InvalidInput, result.Status);
        }
        var missing = await test.Service.ChangeStaffAsync(Guid.NewGuid().ToString(), StaffAccessAction.Grant, test.Actor);
        Assert.Equal(StaffAccessOperationStatus.NotFound, missing.Status);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(StaffAccessAction.Grant)]
    [InlineData(StaffAccessAction.Remove)]
    public async Task AuditFailure_RollsBackMembershipAndSecurityStampAndCannotBeRetriedByLaterSave(StaffAccessAction action)
    {
        await using var test = await TestContext.CreateAsync(targetStaff: action == StaffAccessAction.Remove);
        var before = await test.SnapshotAsync();
        await test.Database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectAdministrativeAudit BEFORE INSERT ON "AdministrativeAuditEvents"
            BEGIN SELECT RAISE(ABORT, 'Synthetic audit rejection'); END;
            """);

        var result = await test.Service.ChangeStaffAsync(test.Target.Id, action, test.Actor);

        Assert.Equal(StaffAccessOperationStatus.PersistenceFailed, result.Status);
        Assert.Equal(before, await test.SnapshotAsync());
        await test.Database.SaveChangesAsync();
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Search_IsLiteralCaseInsensitiveAndReadOnlyWithCurrentMembership()
    {
        await using var test = await TestContext.CreateAsync(targetStaff: true);
        test.Target.DisplayName = "<b>Unique %_! name</b>";
        Assert.True((await test.Users.UpdateAsync(test.Target)).Succeeded);
        var before = await test.SnapshotAsync();
        foreach (var search in new[] { "  uNiQuE  ", "%", "_", "!", test.Target.Email!.ToUpperInvariant() })
        {
            var result = await test.Service.GetPageAsync(new() { Search = search }, test.Actor);
            Assert.Equal(StaffAccessQueryStatus.Success, result.Status);
            var item = Assert.Single(result.Page!.Items);
            Assert.Equal(test.Target.Id, item.Id);
            Assert.True(item.IsStaff);
            Assert.False(item.IsAdministrator);
            Assert.True(item.IsActive);
        }
        var all = await test.Service.GetPageAsync(new() { Search = " \t " }, test.Actor);
        Assert.Equal(2, all.Page!.TotalCount);
        var missing = await test.Service.GetPageAsync(new() { Search = "not-present" }, test.Actor);
        Assert.Empty(missing.Page!.Items);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("long")]
    [InlineData("control")]
    public async Task InvalidSearch_DoesNotReturnAccounts(string invalid)
    {
        await using var test = await TestContext.CreateAsync();
        StaffAccessQueryInput? input = invalid switch
        {
            "null" => null,
            "long" => new() { Search = new string('x', 101) },
            _ => new() { Search = "invalid\0text" }
        };
        var before = await test.SnapshotAsync();
        var result = await test.Service.GetPageAsync(input, test.Actor);
        Assert.Equal(StaffAccessQueryStatus.InvalidInput, result.Status);
        Assert.Null(result.Page);
        Assert.Equal(before, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Pagination_CountsFilteredAccountsAndUsesStableTieBreakAndBoundedPages()
    {
        await using var test = await TestContext.CreateAsync();
        // Missing normalized emails tie; account IDs must still order deterministically.
        var ids = Enumerable.Range(1, 43).Select(index => $"pagination-{index:000}").ToArray();
        test.Database.Users.AddRange(ids.Reverse().Select(id => new ApplicationUser
        {
            Id = id, DisplayName = "Pagination fixture", IsActive = true
        }));
        await test.Database.SaveChangesAsync();
        test.Database.ChangeTracker.Clear();
        var before = await test.SnapshotAsync();
        var pages = new List<StaffAccessPage>();
        for (var number = 1; number <= 3; number++)
            pages.Add((await test.Service.GetPageAsync(new() { Search = "Pagination fixture" }, test.Actor, number)).Page!);
        Assert.Equal(new[] { 20, 20, 3 }, pages.Select(page => page.Items.Count));
        Assert.All(pages, page => Assert.Equal(43, page.TotalCount));
        Assert.Equal(ids, pages.SelectMany(page => page.Items.Select(item => item.Id)));
        Assert.Equal(43, pages.SelectMany(page => page.Items).DistinctBy(item => item.Id).Count());
        foreach (var number in new[] { int.MinValue, 0, int.MaxValue })
        {
            var page = (await test.Service.GetPageAsync(new() { Search = "Pagination fixture" }, test.Actor, number)).Page!;
            Assert.Equal(number <= 0 ? 1 : 3, page.PageNumber);
        }
        Assert.Equal(before, await test.SnapshotAsync());
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, ApplicationRoles.Administrator)], "Test"));

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestContext(AuthorizationWebApplicationFactory factory, AsyncServiceScope scope,
        ApplicationDbContext database, UserManager<ApplicationUser> users, ApplicationUser admin, ApplicationUser target) : IAsyncDisposable
    {
        public ApplicationDbContext Database { get; } = database;
        public UserManager<ApplicationUser> Users { get; } = users;
        public ApplicationUser Admin { get; } = admin;
        public ApplicationUser Target { get; } = target;
        public ClaimsPrincipal Actor { get; } = Principal(admin.Id);
        public StaffAccessService Service { get; } = new(database, users,
            scope.ServiceProvider.GetRequiredService<ILookupNormalizer>(), new Clock(), NullLogger<StaffAccessService>.Instance);

        public static async Task<TestContext> CreateAsync(bool targetStaff = false)
        {
            var factory = new AuthorizationWebApplicationFactory();
            var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            async Task<ApplicationUser> Add(string name, string role)
            {
                var email = IdentityTestContext.NewEmail();
                var user = new ApplicationUser { DisplayName = name, UserName = email, Email = email };
                Assert.True((await users.CreateAsync(user)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                return user;
            }
            var admin = await Add("Synthetic administrator", ApplicationRoles.Administrator);
            var target = await Add("Synthetic target", ApplicationRoles.Student);
            if (targetStaff) Assert.True((await users.AddToRoleAsync(target, ApplicationRoles.Staff)).Succeeded);
            return new(factory, scope, database, users, admin, target);
        }

        public Task<string?> TargetStampAsync() => Database.Users.AsNoTracking().Where(row => row.Id == Target.Id)
            .Select(row => row.SecurityStamp).SingleAsync();
        public Task<string[]> TargetRolesAsync() => (from membership in Database.UserRoles
            join role in Database.Roles on membership.RoleId equals role.Id
            where membership.UserId == Target.Id orderby role.Name select role.Name!).ToArrayAsync();

        public async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
        {
            Accounts = await Database.Users.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            { row.Id, row.DisplayName, row.Email, row.IsActive, row.SecurityStamp, row.ConcurrencyStamp }).ToArrayAsync(),
            Roles = await Database.UserRoles.AsNoTracking().OrderBy(row => row.UserId).ThenBy(row => row.RoleId).ToArrayAsync(),
            Audits = await Database.AdministrativeAuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Listings = await Database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Status }).ToArrayAsync(),
            InventoryAudits = await Database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
        });
        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            factory.Dispose();
        }
    }
}
