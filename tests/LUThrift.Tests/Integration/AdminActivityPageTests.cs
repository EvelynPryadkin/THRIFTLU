using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public sealed class AdminActivityPageTests
{
    private const string Path = "/Admin/Activity";
    private const string ActorId = "actor-<fixture>";
    private const string TargetId = "removed-target-<fixture>";
    private static readonly DateTime Stamp = new(2026, 1, 5, 16, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    [InlineData(ApplicationRoles.Staff, "/Identity/Account/AccessDenied")]
    public async Task NonAdministrator_CannotReadEitherHistory(string? role, string redirect)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(role);
        await SeedAsync(factory);
        var before = await SnapshotAsync(factory);
        foreach (var history in new[] { "Inventory", "Administration" })
        {
            using var response = await client.GetAsync(Path + "?Filter.History=" + history);
            AssertRedirect(client, response, redirect);
            Assert.DoesNotContain("fixture&gt;", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldAdministratorCookie_LosesAccessAfterRevocationOrDeactivation(bool deactivate)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        await SeedAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var adminId = await (from membership in database.UserRoles
                                join role in database.Roles on membership.RoleId equals role.Id
                                where role.Name == ApplicationRoles.Administrator select membership.UserId).SingleAsync();
            if (deactivate)
                await database.Users.Where(user => user.Id == adminId).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
            else
                await database.UserRoles.Where(row => row.UserId == adminId).ExecuteDeleteAsync();
        }
        var before = await SnapshotAsync(factory);
        foreach (var url in new[] { Path, Path + "?Filter.History=Administration", Path + "?Filter.ActorId=" + new string('x', 451) })
        {
            using var response = await client.GetAsync(url);
            AssertRedirect(client, response, "/Identity/Account/AccessDenied");
        }
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task InventoryFilters_PreserveSelectionAndStablePagesAndEncodeContentWithoutWrites()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        await SeedAsync(factory);
        var before = await SnapshotAsync(factory);
        Assert.Contains(Tags(await client.GetStringAsync("/Admin"), "a"), tag => Attribute(tag, "href") == Path);
        var url = QueryHelpers.AddQueryString(Path, new Dictionary<string, string?>
        {
            ["Filter.History"] = "Inventory", ["Filter.Action"] = InventoryAuditActions.Updated,
            ["Filter.ActorId"] = "  " + ActorId + "  ", ["Filter.ListingId"] = "1"
        });
        using var first = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var html = MainContent(await first.Content.ReadAsStringAsync());
        Assert.Equal(Enumerable.Range(6, 20).Reverse(), RowIds(html));
        Assert.Contains("25 records found. Page 1 of 2.", html);
        Assert.Contains("&lt;script&gt;actor&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;listing&lt;/script&gt;", html);
        Assert.Contains("actor-&lt;fixture&gt;", html);
        Assert.DoesNotContain("<script>actor</script>", html);
        Assert.DoesNotContain("<script>listing</script>", html);
        Assert.DoesNotContain("SENSITIVE_PAYLOAD", html);
        Assert.DoesNotContain("/private/photo-store", html);
        Assert.DoesNotContain("fixture-internal@example.test", html);
        Assert.Contains("2026-01-06 01:00:00", html);
        Assert.Contains("Asia/Tokyo, UTC", html);
        Assert.Contains("09:00", html);
        Assert.Equal(ActorId, Attribute(Assert.Single(Tags(html, "input"), tag => Attribute(tag, "name") == "Filter.ActorId"), "value"));
        Assert.All(Tags(html, "form"), form => Assert.Equal("get", Attribute(form, "method")));
        var next = Assert.Single(Tags(html, "a"), tag => Attribute(tag, "rel") == "next");
        var nextUrl = new Uri(client.BaseAddress!, Attribute(next, "href"));
        var query = QueryHelpers.ParseQuery(nextUrl.Query);
        Assert.Equal("Inventory", query["Filter.History"].ToString());
        Assert.Equal(InventoryAuditActions.Updated, query["Filter.Action"].ToString());
        Assert.Equal(ActorId, query["Filter.ActorId"].ToString());
        Assert.Equal("1", query["Filter.ListingId"].ToString());
        Assert.Equal("2", query["pageNumber"].ToString());
        using var second = await client.GetAsync(nextUrl.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(Enumerable.Range(1, 5).Reverse(), RowIds(MainContent(await second.Content.ReadAsStringAsync())));
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task AdministrativeView_ShowsHistoricalTargetsAndSeparateScheduleFilters()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        await SeedAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Audit identifiers survive the current account record becoming unavailable.
            await database.Users.Where(user => user.Id == ActorId).ExecuteDeleteAsync();
        }
        var before = await SnapshotAsync(factory);
        var url = QueryHelpers.AddQueryString(Path, new Dictionary<string, string?>
        {
            ["Filter.History"] = "Administration", ["Filter.TargetUserId"] = TargetId,
            ["Filter.Action"] = AdministrativeAuditActions.StaffGranted, ["Filter.ActorId"] = ActorId
        });
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = MainContent(await response.Content.ReadAsStringAsync());
        Assert.Contains("Administrative activity", html);
        Assert.Contains("removed-target-&lt;fixture&gt;", html);
        Assert.Contains("actor-&lt;fixture&gt;", html);
        Assert.Contains("Account unavailable", html);
        Assert.DoesNotContain("Listing #1", html);
        var next = new Uri(client.BaseAddress!, Attribute(Assert.Single(Tags(html, "a"), tag => Attribute(tag, "rel") == "next"), "href"));
        var nextQuery = QueryHelpers.ParseQuery(next.Query);
        Assert.Equal(TargetId, nextQuery["Filter.TargetUserId"].ToString());
        Assert.Equal("Administration", nextQuery["Filter.History"].ToString());
        using var schedule = await client.GetAsync(Path + "?Filter.History=Administration&Filter.ScheduleId=1");
        Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);
        var scheduleHtml = MainContent(await schedule.Content.ReadAsStringAsync());
        Assert.Contains("Pickup schedule #1", scheduleHtml);
        Assert.Contains("1 record found. Page 1 of 1.", scheduleHtml);
        Assert.DoesNotContain("removed-target-&lt;fixture&gt;", scheduleHtml);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Theory]
    [InlineData("Filter.ActorId", "length")]
    [InlineData("Filter.Action", "unknown")]
    [InlineData("Filter.ListingId", "not-an-id")]
    [InlineData("Filter.ListingId", "-1")]
    [InlineData("Filter.History", "99")]
    [InlineData("Filter.History", "unknown")]
    [InlineData("Filter.TargetUserId", "wrong-view")]
    public async Task InvalidFilters_ReturnSafeValidationWithoutActivity(string field, string value)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        await SeedAsync(factory);
        var before = await SnapshotAsync(factory);
        using var response = await client.GetAsync(QueryHelpers.AddQueryString(Path, field, value == "length" ? new string('x', 451) : value));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = MainContent(await response.Content.ReadAsStringAsync());
        Assert.Contains("validation-summary-errors", html);
        Assert.Empty(RowIds(html));
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task EmptyResultsExtremePagesAndUnknownActions_AreSafeAndReadOnly()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        await SeedAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.AuditEvents.Add(new AuditEvent
            {
                ListingId = 2, ActorUserId = ReservationService.ExpirationActorId, Action = "SENSITIVE_ACTION_PAYLOAD",
                Details = "SENSITIVE_PAYLOAD /private/photo-store", OccurredAtUtc = Stamp.AddDays(1)
            });
            await database.SaveChangesAsync();
        }
        var before = await SnapshotAsync(factory);
        var unknown = MainContent(await client.GetStringAsync(Path + "?Filter.ListingId=2"));
        Assert.Single(RowIds(unknown));
        Assert.Contains(ReservationService.ExpirationActorId, unknown);
        Assert.Contains("System: reservation expiration", unknown);
        Assert.DoesNotContain("SENSITIVE_ACTION_PAYLOAD", unknown);
        Assert.DoesNotContain("SENSITIVE_PAYLOAD", unknown);
        var empty = MainContent(await client.GetStringAsync(Path + "?Filter.ListingId=2147483647"));
        Assert.Contains("No activity matches these filters", empty);
        Assert.Contains("0 records found. Page 1 of 1.", empty);
        foreach (var page in new[] { "-1", "2147483647", "999999999999999999999", "not-a-number" })
        {
            using var response = await client.GetAsync(QueryHelpers.AddQueryString(Path, "pageNumber", page));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    private static async Task SeedAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        database.Users.Add(new ApplicationUser
        {
            Id = ActorId, DisplayName = "<script>actor</script>", Email = "fixture-internal@example.test"
        });
        await database.Listings.Where(listing => listing.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Title, "<script>listing</script>"));
        database.PickupSchedules.Add(new PickupSchedule { Version = Guid.NewGuid(), CampusTimeZone = "Asia/Tokyo" });
        database.AuditEvents.AddRange(Enumerable.Range(1, 25).Select(_ => new AuditEvent
        {
            ListingId = 1, ActorUserId = ActorId, Action = InventoryAuditActions.Updated,
            OccurredAtUtc = Stamp, Details = "SENSITIVE_PAYLOAD /private/photo-store"
        }));
        database.AdministrativeAuditEvents.AddRange(Enumerable.Range(1, 24).Select(_ => new AdministrativeAuditEvent
        {
            ActorUserId = ActorId, TargetUserId = TargetId, Action = AdministrativeAuditActions.StaffGranted,
            OccurredAtUtc = Stamp
        }));
        database.AdministrativeAuditEvents.Add(new AdministrativeAuditEvent
        {
            ActorUserId = ActorId, TargetScheduleId = 1, Action = AdministrativeAuditActions.PickupScheduleSaved,
            OccurredAtUtc = Stamp.AddMinutes(1)
        });
        await database.SaveChangesAsync();
    }

    private static int[] RowIds(string html) => Regex.Matches(html, "data-activity-id=\"([0-9]+)\"")
        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
    private static string MainContent(string html) => Regex.Match(html, @"<main\b[^>]*>([\s\S]*?)</main>", RegexOptions.IgnoreCase).Groups[1].Value;
    private static string[] Tags(string html, string tag) => Regex.Matches(html, $@"<{tag}\b[^>]*>", RegexOptions.IgnoreCase).Select(match => match.Value).ToArray();
    private static string Attribute(string tag, string name) => WebUtility.HtmlDecode(Regex.Match(tag,
        "\\b" + Regex.Escape(name) + "=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
    private static void AssertRedirect(HttpClient client, HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(path, new Uri(client.BaseAddress!, response.Headers.Location!).AbsolutePath);
    }
    private static async Task<string> SnapshotAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return JsonSerializer.Serialize(new
        {
            Accounts = await database.Users.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.IsActive, row.DisplayName, row.SecurityStamp, row.ConcurrencyStamp }).ToArrayAsync(),
            Memberships = await database.UserRoles.AsNoTracking().OrderBy(row => row.UserId).ThenBy(row => row.RoleId).ToArrayAsync(),
            Inventory = await database.Listings.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Title, row.Status, row.CreatedAtUtc }).ToArrayAsync(),
            Reservations = await database.Reservations.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.UserId, row.Status, row.ExpiresAtUtc, row.CreatedAtUtc, row.FulfilledAtUtc, row.CancellationReason }).ToArrayAsync(),
            Audits = await database.AuditEvents.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.ActorUserId, row.ListingId, row.Action, row.OccurredAtUtc, row.Details }).ToArrayAsync(),
            AdministrativeAudits = await database.AdministrativeAuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Schedules = await database.PickupSchedules.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Version, row.CampusTimeZone }).ToArrayAsync()
        });
    }
}
