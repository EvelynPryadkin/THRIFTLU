using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public class StaffInventoryLifecycleTests
{
    private const string DashboardPath = "/Staff/Inventory";

    [Fact]
    public async Task Dashboard_OffersOnlyApplicablePostActionsWithAntiforgeryAndNoProtectedInputs()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await database.Listings.ExecuteDeleteAsync();
            database.Listings.AddRange(Enum.GetValues<ListingStatus>().Select(status => new Listing
            {
                Title = $"Lifecycle {status}", Description = "Inventory lifecycle test.",
                Condition = "Good", CategoryId = 1, Status = status
            }));
            await database.SaveChangesAsync();
        }
        var listings = await ListingsAsync(factory);

        var html = await DashboardAsync(client);
        var rows = Regex.Matches(html, @"<tr\b[^>]*>[\s\S]*?</tr>", RegexOptions.IgnoreCase).Cast<Match>();

        foreach (var listing in listings)
        {
            var row = Assert.Single(rows, match => match.Value.Contains(listing.Title, StringComparison.Ordinal)).Value;
            var forms = Forms(row);
            string[] expected = listing.Status switch
            {
                ListingStatus.Draft => ["Archive", "Publish"],
                ListingStatus.Available => ["Archive", "MarkMissing", "RecordWalkInCollection"],
                ListingStatus.Missing => ["Archive"],
                _ => []
            };
            Assert.Equal(expected, forms.Select(form => Query(client, form)["handler"].ToString())
                .OrderBy(handler => handler, StringComparer.Ordinal));
            foreach (var form in forms)
            {
                Assert.Equal("post", Attribute(form, "method"), ignoreCase: true);
                Assert.Equal(listing.Id.ToString(CultureInfo.InvariantCulture), Query(client, form)["id"].ToString());
                var inputs = Regex.Matches(form, @"<(?:input|select|textarea)\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>();
                Assert.Equal("__RequestVerificationToken", Attribute(Assert.Single(inputs).Value, "name"));
                Assert.NotEmpty(Token(form));
            }
        }
    }

    [Theory]
    [InlineData("Publish", ListingStatus.Draft, ListingStatus.Available, InventoryAuditActions.Published, "Listing published.")]
    [InlineData("Archive", ListingStatus.Missing, ListingStatus.Archived, InventoryAuditActions.Archived, "Listing archived.")]
    [InlineData("MarkMissing", ListingStatus.Available, ListingStatus.Missing, InventoryAuditActions.MarkedMissing, "Listing marked as missing.")]
    [InlineData("RecordWalkInCollection", ListingStatus.Available, ListingStatus.Collected, InventoryAuditActions.WalkInCollected, "Walk-in collection recorded.")]
    public async Task Action_UsesNamedTransitionAndAuthenticatedActorDespiteForgedFields(
        string handler, ListingStatus initial, ListingStatus expected, string auditAction, string message)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        await SetStatusAsync(factory, initial);
        var before = await ListingsAsync(factory);
        var form = ActionForm(client, await DashboardAsync(client), handler, 1);
        var fields = ForgedFields(Token(form));
        var startedAt = DateTime.UtcNow;
        using var content = new FormUrlEncodedContent(fields);

        using var response = await client.PostAsync(Attribute(form, "action"), content);

        AssertRedirect(client, response, DashboardPath);
        var html = await DashboardAsync(client);
        Assert.Contains(message, WebUtility.HtmlDecode(html));
        Assert.Contains("role=\"status\"", html);
        Assert.DoesNotContain(message, WebUtility.HtmlDecode(await DashboardAsync(client)));
        var after = await ListingsAsync(factory);
        Assert.Equal(before.Length, after.Length);
        var changed = Assert.Single(after, listing => listing.Id == 1);
        Assert.Equal(expected, changed.Status);
        before.Single(listing => listing.Id == 1).Status = expected;
        Assert.Equal(before.Select(Snapshot), after.Select(Snapshot));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditEvents.SingleAsync();
        Assert.Equal(1, audit.ListingId);
        Assert.Equal(auditAction, audit.Action);
        Assert.Equal(await database.Users.Select(user => user.Id).SingleAsync(), audit.ActorUserId);
        Assert.InRange(audit.OccurredAtUtc, startedAt, DateTime.UtcNow);
        Assert.DoesNotContain("forged", audit.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Publish", ListingStatus.Draft)]
    [InlineData("Archive", ListingStatus.Missing)]
    [InlineData("MarkMissing", ListingStatus.Available)]
    [InlineData("RecordWalkInCollection", ListingStatus.Available)]
    public async Task GetWithActionHandler_NeverChangesInventory(string handler, ListingStatus initial)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        await SetStatusAsync(factory, initial);
        var before = await ListingsAsync(factory);

        using var response = await client.GetAsync(ActionPath(handler, 1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertNoWritesAsync(factory, before);
    }

    [Fact]
    public async Task Action_RejectsMissingAntiforgeryTokenWithoutWrites()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        await SetStatusAsync(factory, ListingStatus.Draft);
        var before = await ListingsAsync(factory);
        var form = ActionForm(client, await DashboardAsync(client), "Publish", 1);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>());

        using var response = await client.PostAsync(Attribute(form, "action"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoWritesAsync(factory, before);
    }

    [Theory]
    [InlineData(null, "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    public async Task Action_PostEnforcesStaffFolderPolicy(string? role, string redirectPath)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(role);
        await SetStatusAsync(factory, ListingStatus.Draft);
        var before = await ListingsAsync(factory);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>());

        using var response = await client.PostAsync(ActionPath("Publish", 1), content);

        AssertRedirect(client, response, redirectPath);
        await AssertNoWritesAsync(factory, before);
    }

    [Theory]
    [InlineData(false, "The listing could not be found.")]
    [InlineData(true, "This action is not available for the listing's current status.")]
    public async Task MissingOrNewlyHeldListing_ReturnsSafeErrorAndCannotBeUnlockedByForgedStatus(bool held, string message)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        await SetStatusAsync(factory, ListingStatus.Draft);
        var form = ActionForm(client, await DashboardAsync(client), "Publish", 1);
        if (held)
        {
            await SetStatusAsync(factory, ListingStatus.Held);
        }
        var before = await ListingsAsync(factory);
        using var content = new FormUrlEncodedContent(ForgedFields(Token(form)));

        using var response = await client.PostAsync(ActionPath("Publish", held ? 1 : int.MaxValue), content);

        AssertRedirect(client, response, DashboardPath);
        var html = await DashboardAsync(client);
        Assert.Contains(message, WebUtility.HtmlDecode(html));
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("Listing published.", html);
        await AssertNoWritesAsync(factory, before);
    }

    [Fact]
    public async Task AuditWriteFailure_ShowsSafeErrorAndRollsBackStatus()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Administrator);
        await SetStatusAsync(factory, ListingStatus.Draft);
        var before = await ListingsAsync(factory);
        var form = ActionForm(client, await DashboardAsync(client), "Publish", 1);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectLifecycleAudit BEFORE INSERT ON "AuditEvents"
                BEGIN SELECT RAISE(ABORT, 'Lifecycle audit test failure'); END;
                """);
        }
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(form)
        });

        using var response = await client.PostAsync(Attribute(form, "action"), content);

        AssertRedirect(client, response, DashboardPath);
        var html = await DashboardAsync(client);
        Assert.Contains("We couldn't save the inventory change. Please try again.", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("Lifecycle audit test failure", html);
        Assert.DoesNotContain("SqliteException", html);
        Assert.DoesNotContain("Listing published.", html);
        await AssertNoWritesAsync(factory, before);
    }

    private static Dictionary<string, string> ForgedFields(string token) => new()
    {
        ["__RequestVerificationToken"] = token,
        ["Status"] = "not-a-status",
        ["Input.Status"] = nameof(ListingStatus.Draft),
        ["newStatus"] = nameof(ListingStatus.Archived),
        ["ActorUserId"] = "forged-actor",
        ["AuditEvents[0].Details"] = "forged details",
        ["id"] = "2"
    };

    private static string ActionPath(string handler, int id)
        => QueryHelpers.AddQueryString(DashboardPath, new Dictionary<string, string?>
        {
            ["handler"] = handler, ["id"] = id.ToString(CultureInfo.InvariantCulture)
        });

    private static async Task<string> DashboardAsync(HttpClient client)
    {
        using var response = await client.GetAsync(DashboardPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string[] Forms(string html)
        => Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => match.Value).ToArray();

    private static string ActionForm(HttpClient client, string html, string handler, int id)
        => Assert.Single(Forms(html), form => Query(client, form).GetValueOrDefault("handler").ToString() == handler
            && Query(client, form)["id"].ToString() == id.ToString(CultureInfo.InvariantCulture));

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Query(HttpClient client, string form)
        => QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, Attribute(form, "action")).Query);

    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);

    private static string Token(string form)
        => Attribute(Assert.Single(Regex.Matches(form, @"<input\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>(),
            input => Attribute(input.Value, "name") == "__RequestVerificationToken").Value, "value");

    private static void AssertRedirect(HttpClient client, HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(path, new Uri(client.BaseAddress!, response.Headers.Location).AbsolutePath);
    }

    private static async Task SetStatusAsync(AuthorizationWebApplicationFactory factory, ListingStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Listings.Where(listing => listing.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, status));
    }

    private static async Task<Listing[]> ListingsAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Listings
            .AsNoTracking().OrderBy(listing => listing.Id).ToArrayAsync();
    }

    private static async Task AssertNoWritesAsync(AuthorizationWebApplicationFactory factory, Listing[] before)
    {
        Assert.Equal(before.Select(Snapshot), (await ListingsAsync(factory)).Select(Snapshot));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AuditEvents.ToListAsync());
    }

    private static object Snapshot(Listing listing)
        => (listing.Id, listing.Title, listing.Description, listing.Condition, listing.Size,
            listing.CategoryId, listing.Status, listing.CreatedAtUtc);
}
