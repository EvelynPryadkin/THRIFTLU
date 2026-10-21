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

public class CatalogWorkflowTests
{
    [Theory]
    [InlineData("MarkMissing", ListingStatus.Missing, InventoryAuditActions.MarkedMissing)]
    [InlineData("RecordWalkInCollection", ListingStatus.Collected, InventoryAuditActions.WalkInCollected)]
    [InlineData("Archive", ListingStatus.Archived, InventoryAuditActions.Archived)]
    public async Task StaffLifecycleChanges_AppearImmediatelyToStudentsWithoutBrowsingWrites(
        string handler, ListingStatus finalStatus, string finalAuditAction)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var staff = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        using var student = await factory.CreateIdentityClientAsync(ApplicationRoles.Student);
        var listing = NewListing($"Lifecycle catalog item {Guid.NewGuid():N}", ListingStatus.Draft);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Listings.Add(listing);
            await database.SaveChangesAsync();
        }

        await AssertStudentVisibilityWithoutWritesAsync(factory, student, listing, visible: false);
        await SubmitStaffActionAsync(staff, "Publish", listing.Id);
        var savedDetailsLink = await AssertStudentVisibilityWithoutWritesAsync(factory, student, listing, visible: true);
        await SubmitStaffActionAsync(staff, handler, listing.Id);
        await AssertStudentVisibilityWithoutWritesAsync(factory, student, listing, visible: false, savedDetailsLink);

        await using var verification = factory.Services.CreateAsyncScope();
        var persisted = verification.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(finalStatus, await persisted.Listings.Where(row => row.Id == listing.Id).Select(row => row.Status).SingleAsync());
        var audits = await persisted.AuditEvents.AsNoTracking().OrderBy(audit => audit.Id).ToArrayAsync();
        Assert.Equal(new[] { InventoryAuditActions.Published, finalAuditAction }, audits.Select(audit => audit.Action));
        var staffId = await (from membership in persisted.UserRoles
                             join role in persisted.Roles on membership.RoleId equals role.Id
                             where role.Name == ApplicationRoles.Staff
                             select membership.UserId).SingleAsync();
        Assert.All(audits, audit =>
        {
            Assert.Equal(listing.Id, audit.ListingId);
            Assert.Equal(staffId, audit.ActorUserId);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ApplicationRoles.Student)]
    public async Task FilteredPageDetailsAndBack_StayPublicAndDoNotWriteInventoryOrHistory(string? role)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(role);
        var listings = Enumerable.Range(1, 13).Select(index =>
        {
            var listing = NewListing($"Workflow 100%_ item {index}", index == 1 ? ListingStatus.Held : ListingStatus.Available);
            listing.Id = 200 + index;
            listing.Size = "M & tall";
            return listing;
        }).ToArray();
        var held = listings[0];
        var owner = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = $"private-owner-{Guid.NewGuid():N}",
            Email = $"private-{Guid.NewGuid():N}@example.test",
            DisplayName = $"Private owner {Guid.NewGuid():N}"
        };
        var audit = new AuditEvent
        {
            ListingId = held.Id,
            ActorUserId = owner.Id,
            Action = "WorkflowSeedAudit",
            Details = $"Private workflow history {Guid.NewGuid():N}",
            OccurredAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Listings.AddRange(listings);
            database.Users.Add(owner);
            database.AuditEvents.Add(audit);
            database.Reservations.Add(new Reservation
            {
                ListingId = held.Id, UserId = owner.Id, Status = ReservationStatus.Active,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5), ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
            await database.SaveChangesAsync();
        }
        var before = await SnapshotAsync(factory);
        var filters = new Dictionary<string, string?>
        {
            ["Filter.Search"] = "Workflow 100%_", ["Filter.CategoryId"] = "1",
            ["Filter.Size"] = "M & tall", ["Filter.Availability"] = "2"
        };

        var first = await GetHtmlAsync(client, QueryHelpers.AddQueryString("/Thrift", filters));
        Assert.Equal(12, Cards(first).Length);
        var second = await GetHtmlAsync(client, Link(first, "Next"));
        var heldCard = Assert.Single(Cards(second));
        Assert.Contains(held.Title, heldCard);
        var details = await GetHtmlAsync(client, Attribute(Assert.Single(Tags(heldCard, "a")), "href"));
        Assert.Contains(held.Title, details);
        Assert.Matches(@">\s*Reserved\s*</span>", details);
        foreach (var privateValue in new[] { owner.Id, owner.UserName, owner.Email, owner.DisplayName, audit.Details })
        {
            Assert.DoesNotContain(privateValue, WebUtility.HtmlDecode(details));
        }
        var back = Link(details, "Back to catalog");
        var returnQuery = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, back).Query);
        foreach (var (key, value) in filters)
        {
            Assert.Equal(value, returnQuery[key].ToString());
        }
        Assert.Equal("2", returnQuery["pageNumber"].ToString());
        var returned = await GetHtmlAsync(client, back);
        Assert.Equal(Cards(second), Cards(returned));
        await GetHtmlAsync(client, Attribute(Assert.Single(Tags(Cards(first)[0], "a")), "href"));
        using var invalid = await client.GetAsync("/Thrift?Filter.CategoryId=0&pageNumber=2");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await AssertUnchangedAsync(factory, before);
    }

    [Fact]
    public async Task InvalidDetailsRoutes_ReturnNotFoundWithoutWritesOrAuthenticationRedirects()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var before = await SnapshotAsync(factory);

        foreach (var path in new[]
        {
            "/Thrift/Details", "/Thrift/Details/not-an-id", "/Thrift/Details/2147483648",
            "/Thrift/Details/0", "/Thrift/Details/-1"
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.DoesNotContain("Blue hoodie", await response.Content.ReadAsStringAsync());
        }

        await AssertUnchangedAsync(factory, before);
    }

    [Fact]
    public async Task CatalogNavigationToggle_TargetsItsAccessibleCollapseContainer()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();

        var html = await GetHtmlAsync(client, "/Thrift");

        var toggle = Assert.Single(Tags(html, "button"), button => Attribute(button, "data-bs-toggle") == "collapse");
        var controlledId = Attribute(toggle, "aria-controls");
        Assert.NotEmpty(controlledId);
        Assert.Equal("#" + controlledId, Attribute(toggle, "data-bs-target"));
        var container = Assert.Single(Regex.Matches(html, @"<div\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>(),
            element => Attribute(element.Value, "id") == controlledId);
        var classes = Attribute(container.Value, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("collapse", classes);
        Assert.Contains("navbar-collapse", classes);
    }

    private static async Task<string> AssertStudentVisibilityWithoutWritesAsync(
        AuthorizationWebApplicationFactory factory, HttpClient student, Listing listing, bool visible, string? savedDetailsLink = null)
    {
        var before = await SnapshotAsync(factory);
        var catalog = await GetHtmlAsync(student, QueryHelpers.AddQueryString("/Thrift", new Dictionary<string, string?>
        {
            ["Filter.Search"] = listing.Title, ["Filter.Availability"] = "2"
        }));
        var detailsLink = savedDetailsLink ?? $"/Thrift/Details/{listing.Id}";
        if (visible)
        {
            var card = Assert.Single(Cards(catalog));
            Assert.Contains(listing.Title, card);
            detailsLink = Attribute(Assert.Single(Tags(card, "a")), "href");
        }
        else
        {
            Assert.Empty(Cards(catalog));
        }
        using var response = await student.GetAsync(detailsLink);
        Assert.Equal(visible ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
        if (visible)
        {
            Assert.Contains(listing.Title, await response.Content.ReadAsStringAsync());
        }
        await AssertUnchangedAsync(factory, before);
        return detailsLink;
    }

    private static async Task SubmitStaffActionAsync(HttpClient staff, string handler, int listingId)
    {
        var dashboard = await GetHtmlAsync(staff, "/Staff/Inventory");
        var form = Assert.Single(Tags(dashboard, "form"), form =>
        {
            var query = QueryHelpers.ParseQuery(new Uri(staff.BaseAddress!, Attribute(form, "action")).Query);
            return query.GetValueOrDefault("handler").ToString() == handler
                && query.GetValueOrDefault("id").ToString() == listingId.ToString(CultureInfo.InvariantCulture);
        });
        var token = Assert.Single(Regex.Matches(form, @"<input\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>(),
            input => Attribute(input.Value, "name") == "__RequestVerificationToken");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Attribute(token.Value, "value")
        });
        using var response = await staff.PostAsync(Attribute(form, "action"), content);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/Staff/Inventory", new Uri(staff.BaseAddress!, response.Headers.Location).AbsolutePath);
    }

    private static Listing NewListing(string title, ListingStatus status) => new()
    {
        Title = title, Description = "Catalog workflow test listing.", Condition = "Good", Size = "M",
        CategoryId = 1, Status = status, CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static async Task<DatabaseSnapshot> SnapshotAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var listings = await database.Listings.AsNoTracking().OrderBy(listing => listing.Id).ToArrayAsync();
        var audits = await database.AuditEvents.AsNoTracking().OrderBy(audit => audit.Id).ToArrayAsync();
        var reservations = await database.Reservations.AsNoTracking().OrderBy(reservation => reservation.Id).ToArrayAsync();
        return new DatabaseSnapshot(
            listings.Select(listing => (object)(listing.Id, listing.Title, listing.Description, listing.Condition,
                listing.Size, listing.CategoryId, listing.Status, listing.CreatedAtUtc)).ToArray(),
            audits.Select(audit => (object)(audit.Id, audit.ListingId, audit.ActorUserId, audit.Action,
                audit.Details, audit.OccurredAtUtc)).ToArray(),
            reservations.Select(reservation => (object)(reservation.Id, reservation.ListingId, reservation.UserId,
                reservation.Status, reservation.CreatedAtUtc, reservation.ExpiresAtUtc,
                reservation.FulfilledAtUtc, reservation.CancellationReason)).ToArray());
    }

    private static async Task AssertUnchangedAsync(AuthorizationWebApplicationFactory factory, DatabaseSnapshot before)
    {
        var after = await SnapshotAsync(factory);
        Assert.Equal(before.Listings, after.Listings);
        Assert.Equal(before.Audits, after.Audits);
        Assert.Equal(before.Reservations, after.Reservations);
    }

    private sealed record DatabaseSnapshot(object[] Listings, object[] Audits, object[] Reservations);

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string[] Cards(string html) => Tags(html, "article");

    private static string Link(string html, string label)
        => Attribute(Assert.Single(Tags(html, "a"), link => Regex.IsMatch(link,
            $@">\s*{Regex.Escape(label)}\s*</a>", RegexOptions.IgnoreCase)), "href");

    private static string[] Tags(string html, string tag)
        => Regex.Matches(html, $@"<{tag}\b[^>]*>[\s\S]*?</{tag}>", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => match.Value).ToArray();

    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
}
