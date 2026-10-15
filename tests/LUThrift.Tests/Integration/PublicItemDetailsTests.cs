using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public class PublicItemDetailsTests
{
    [Theory]
    [InlineData(ListingStatus.Draft, HttpStatusCode.NotFound)]
    [InlineData(ListingStatus.Available, HttpStatusCode.OK)]
    [InlineData(ListingStatus.Held, HttpStatusCode.OK)]
    [InlineData(ListingStatus.Collected, HttpStatusCode.NotFound)]
    [InlineData(ListingStatus.Missing, HttpStatusCode.NotFound)]
    [InlineData(ListingStatus.Archived, HttpStatusCode.NotFound)]
    public async Task Details_EnforcesVisibilityForAnonymousVisitors(ListingStatus status, HttpStatusCode expected)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var listing = NewListing(status);
        listing.Size = status == ListingStatus.Held ? null : "M";
        await AddListingsAsync(factory, listing);

        using var response = await client.GetAsync(DetailsPath(listing.Id));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.Null(response.Headers.Location);
        if (expected == HttpStatusCode.OK)
        {
            var main = MainContent(html);
            var decoded = WebUtility.HtmlDecode(main);
            Assert.Contains(listing.Title, decoded);
            Assert.Contains(listing.Description, decoded);
            Assert.Contains("Clothing", decoded);
            Assert.Contains(listing.Condition, decoded);
            Assert.Contains(listing.Size ?? "Not specified", decoded);
            var displayStatus = status == ListingStatus.Held ? "Reserved" : "Available";
            Assert.Matches($@">\s*{displayStatus}\s*</", main);
        }
        else
        {
            Assert.DoesNotContain(listing.Title, html);
            Assert.DoesNotContain(listing.Description, html);
        }
    }

    [Fact]
    public async Task Details_UnknownListingReturnsNotFoundWithoutRedirect()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();

        using var response = await client.GetAsync(DetailsPath(int.MaxValue));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Details_EncodesFullLongTextAndMetadataWithLocalImageAndCatalogLink()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        const string titlePrefix = "<b>Long item title</b> ";
        const string descriptionPrefix = "<script>alert('details')</script>\n";
        var listing = NewListing(ListingStatus.Available);
        listing.Title = titlePrefix + new string('t', 150 - titlePrefix.Length);
        listing.Description = descriptionPrefix + new string('d', 2000 - descriptionPrefix.Length);
        listing.Condition = "<em>Gently used</em>";
        listing.Size = "<small>M</small>";
        listing.Category = new Category { Name = "<strong>Details category</strong>", DisplayOrder = 10 };
        listing.CategoryId = 0;
        await AddListingsAsync(factory, listing);

        using var response = await client.GetAsync(DetailsPath(listing.Id));
        var main = MainContent(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decoded = WebUtility.HtmlDecode(main);
        foreach (var value in new[] { listing.Title, listing.Description, listing.Category.Name, listing.Condition, listing.Size })
        {
            Assert.Contains(value, decoded);
            Assert.DoesNotContain(value, main);
        }
        Assert.DoesNotContain("<script", main, StringComparison.OrdinalIgnoreCase);
        var heading = Assert.Single(Regex.Matches(main, @"<h1\b[^>]*>([\s\S]*?)</h1>", RegexOptions.IgnoreCase).Cast<Match>());
        Assert.Equal(listing.Title, WebUtility.HtmlDecode(heading.Groups[1].Value).Trim());
        var image = Assert.Single(Tags(main, "img"));
        Assert.Equal("No image available", Attribute(image, "alt"));
        var imagePath = Attribute(image, "src");
        Assert.Matches(@"^/images/listing-placeholder(?:\.[A-Za-z0-9_-]+)?\.svg$", imagePath);
        using var imageResponse = await client.GetAsync(imagePath);
        Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
        Assert.Equal("image/svg+xml", imageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains(Tags(main, "a"), link => Attribute(link, "href") is "/Thrift" or "/Thrift/Index");
        Assert.Contains("Back to catalog", decoded);
    }

    [Fact]
    public async Task RepeatedCatalogAndDetailsReads_PreserveDataAndKeepReservationOwnerAndAuditsPrivate()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var available = NewListing(ListingStatus.Available);
        var held = NewListing(ListingStatus.Held);
        await AddListingsAsync(factory, available, held);
        var owner = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = $"private-user-{Guid.NewGuid():N}",
            Email = $"private-{Guid.NewGuid():N}@example.test",
            DisplayName = $"Private owner {Guid.NewGuid():N}"
        };
        var audit = new AuditEvent
        {
            ListingId = held.Id,
            ActorUserId = Guid.NewGuid().ToString(),
            Action = $"PrivateAudit{Guid.NewGuid():N}",
            Details = $"Private audit detail {Guid.NewGuid():N}",
            OccurredAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Users.Add(owner);
            database.AuditEvents.Add(audit);
            database.Reservations.Add(new Reservation
            {
                ListingId = held.Id,
                UserId = owner.Id,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                Status = ReservationStatus.Active
            });
            await database.SaveChangesAsync();
        }
        var before = await SnapshotAsync(factory);
        string[] privateValues = [owner.Id, owner.UserName, owner.Email, owner.DisplayName, audit.ActorUserId, audit.Action, audit.Details];

        for (var iteration = 0; iteration < 2; iteration++)
        {
            foreach (var path in new[] { "/Thrift", DetailsPath(available.Id), DetailsPath(held.Id) })
            {
                using var response = await client.GetAsync(path);
                var decoded = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                foreach (var value in privateValues)
                {
                    Assert.DoesNotContain(value, decoded);
                }
                if (path == DetailsPath(held.Id))
                {
                    Assert.Contains("Reserved", decoded);
                }
                else if (path == "/Thrift")
                {
                    Assert.DoesNotContain(held.Title, decoded);
                }
            }
        }

        var after = await SnapshotAsync(factory);
        Assert.Equal(before.Listings, after.Listings);
        Assert.Equal(before.Audits, after.Audits);
        Assert.Equal(before.Reservations, after.Reservations);
    }

    [Fact]
    public async Task Details_UsesRouteIdAndCannotExposeHiddenListingThroughQueryId()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var available = NewListing(ListingStatus.Available);
        var held = NewListing(ListingStatus.Held);
        var hidden = NewListing(ListingStatus.Draft);
        await AddListingsAsync(factory, available, held, hidden);

        using var visibleResponse = await client.GetAsync($"{DetailsPath(available.Id)}?id={held.Id}");
        var visibleHtml = await visibleResponse.Content.ReadAsStringAsync();
        using var hiddenResponse = await client.GetAsync($"{DetailsPath(hidden.Id)}?id={available.Id}");
        var hiddenHtml = await hiddenResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, visibleResponse.StatusCode);
        Assert.Contains(available.Title, visibleHtml);
        Assert.DoesNotContain(held.Title, visibleHtml);
        Assert.Equal(HttpStatusCode.NotFound, hiddenResponse.StatusCode);
        Assert.DoesNotContain(hidden.Title, hiddenHtml);
        Assert.DoesNotContain(available.Title, hiddenHtml);
    }

    private static string DetailsPath(int id) => $"/Thrift/Details/{id}";

    private static Listing NewListing(ListingStatus status) => new()
    {
        Title = $"Details item {status} {Guid.NewGuid():N}",
        Description = $"Public description {Guid.NewGuid():N}",
        Condition = "Gently used",
        Size = "M",
        CategoryId = 1,
        Status = status,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static async Task AddListingsAsync(AuthorizationWebApplicationFactory factory, params Listing[] listings)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        database.Listings.AddRange(listings);
        await database.SaveChangesAsync();
    }

    private static async Task<DatabaseSnapshot> SnapshotAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var listings = await database.Listings.AsNoTracking().OrderBy(listing => listing.Id).ToArrayAsync();
        var audits = await database.AuditEvents.AsNoTracking().OrderBy(audit => audit.Id).ToArrayAsync();
        var reservations = await database.Reservations.AsNoTracking().OrderBy(reservation => reservation.Id).ToArrayAsync();
        return new DatabaseSnapshot(
            listings.Select(listing => (object)(listing.Id, listing.Title, listing.Description, listing.CategoryId,
                listing.Condition, listing.Size, listing.Status, listing.CreatedAtUtc)).ToArray(),
            audits.Select(audit => (object)(audit.Id, audit.ListingId, audit.ActorUserId, audit.Action,
                audit.OccurredAtUtc, audit.Details)).ToArray(),
            reservations.Select(reservation => (object)(reservation.Id, reservation.ListingId, reservation.UserId,
                reservation.CreatedAtUtc, reservation.ExpiresAtUtc, reservation.Status, reservation.FulfilledAtUtc,
                reservation.CancellationReason)).ToArray());
    }

    private sealed record DatabaseSnapshot(object[] Listings, object[] Audits, object[] Reservations);

    private static string MainContent(string html)
    {
        var main = Regex.Match(html, @"<main\b[^>]*>([\s\S]*?)</main>", RegexOptions.IgnoreCase);
        Assert.True(main.Success);
        return main.Groups[1].Value;
    }

    private static string[] Tags(string html, string tag)
        => Regex.Matches(html, $@"<{tag}\b[^>]*>", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => match.Value).ToArray();

    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
}
