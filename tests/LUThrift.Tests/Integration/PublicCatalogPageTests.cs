using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public class PublicCatalogPageTests
{
    private const string CatalogPath = "/Thrift";

    [Theory]
    [InlineData(null)]
    [InlineData(ApplicationRoles.Student)]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task Catalog_IsPublicForAnonymousAndEveryRole(string? role)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(role);

        using var response = await client.GetAsync(CatalogPath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("Blue hoodie", html);
        Assert.NotEmpty(Cards(html));
    }

    [Fact]
    public async Task Catalog_RendersOnlyAvailableCardsWithEncodedMetadataAndAccessibleLocalPlaceholder()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        const string titlePrefix = "<script>alert('catalog')</script> ";
        var available = NewListing(ListingStatus.Available, titlePrefix + new string('x', 150 - titlePrefix.Length));
        available.Condition = "<strong>Gently used</strong>";
        available.Size = "<span>M</span>";
        var noSize = NewListing(ListingStatus.Available, "Available item without a size");
        noSize.Size = null;
        var hidden = Enum.GetValues<ListingStatus>().Where(status => status != ListingStatus.Available)
            .Select(status => NewListing(status, $"Hidden {status} item")).ToArray();
        var category = new Category { Name = "<em>Catalog category</em>", DisplayOrder = 10 };
        Listing[] listings = [available, noSize, .. hidden];
        foreach (var listing in listings)
        {
            listing.Category = category;
            listing.CategoryId = 0;
        }
        await ReplaceListingsAsync(factory, listings);

        using var response = await client.GetAsync(CatalogPath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cards = Cards(html);
        Assert.Equal(2, cards.Length);
        string? placeholderPath = null;
        foreach (var listing in new[] { available, noSize })
        {
            var card = Assert.Single(cards, card => CardTitle(card) == listing.Title);
            var decoded = WebUtility.HtmlDecode(card);
            Assert.Contains(category.Name, decoded);
            Assert.Contains(listing.Condition, decoded);
            Assert.Contains(listing.Size ?? "Not specified", decoded);
            Assert.Contains("Available", decoded);
            Assert.DoesNotContain(category.Name, card);
            var detailsLink = Assert.Single(Regex.Matches(card, @"<a\b[^>]*>([\s\S]*?)</a>",
                RegexOptions.IgnoreCase).Cast<Match>());
            Assert.Equal("View details", WebUtility.HtmlDecode(detailsLink.Groups[1].Value).Trim());
            var detailsPath = Attribute(detailsLink.Value, "href");
            Assert.Equal($"/Thrift/Details/{listing.Id}", detailsPath);
            Assert.Contains(listing.Title, Attribute(detailsLink.Value, "aria-label"));
            using var detailsResponse = await client.GetAsync(detailsPath);
            Assert.Equal(HttpStatusCode.OK, detailsResponse.StatusCode);
            var detailsHtml = await detailsResponse.Content.ReadAsStringAsync();
            var detailsHeading = Assert.Single(Regex.Matches(detailsHtml, @"<h1\b[^>]*>([\s\S]*?)</h1>",
                RegexOptions.IgnoreCase).Cast<Match>());
            Assert.Equal(listing.Title, WebUtility.HtmlDecode(detailsHeading.Groups[1].Value).Trim());
            var heading = Assert.Single(Tags(card, "h[1-6]"));
            Assert.Equal(Attribute(card, "aria-labelledby"), Attribute(heading, "id"));
            var image = Assert.Single(Tags(card, "img"));
            Assert.Equal("No image available", Attribute(image, "alt"));
            var imagePath = Attribute(image, "src");
            Assert.Matches(@"^/images/listing-placeholder(?:\.[A-Za-z0-9_-]+)?\.svg$", imagePath);
            placeholderPath ??= imagePath;
            Assert.Equal(placeholderPath, imagePath);
        }
        var encodedCard = Assert.Single(cards, card => CardTitle(card) == available.Title);
        Assert.DoesNotContain(available.Title, encodedCard);
        Assert.DoesNotContain(available.Condition, encodedCard);
        Assert.DoesNotContain(available.Size!, encodedCard);
        Assert.DoesNotContain("<script", encodedCard, StringComparison.OrdinalIgnoreCase);
        foreach (var listing in hidden)
        {
            Assert.DoesNotContain(listing.Title, html);
        }
        Assert.NotNull(placeholderPath);
        using var placeholder = await client.GetAsync(placeholderPath);
        Assert.Equal(HttpStatusCode.OK, placeholder.StatusCode);
        Assert.Equal("image/svg+xml", placeholder.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_ShowsEmptyStateForEmptyOrHiddenOnlyInventory(bool includeHiddenListings)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var listings = includeHiddenListings
            ? Enum.GetValues<ListingStatus>().Where(status => status != ListingStatus.Available)
                .Select(status => NewListing(status, $"Hidden {status} item")).ToArray()
            : [];
        await ReplaceListingsAsync(factory, listings);

        using var response = await client.GetAsync(CatalogPath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No items are available right now.", html);
        Assert.Empty(Cards(html));
        foreach (var listing in listings)
        {
            Assert.DoesNotContain(listing.Title, html);
        }
    }

    [Fact]
    public async Task Catalog_RendersLatestTwelveCardsInServiceOrder()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var listings = Enumerable.Range(1, 14).Select(index =>
        {
            var listing = NewListing(ListingStatus.Available, $"Catalog item {index:D2}");
            listing.Id = 100 + index;
            listing.CreatedAtUtc = origin.AddDays(index >= 13 ? 20 : index);
            return listing;
        }).ToArray();
        var held = NewListing(ListingStatus.Held, "Newer held item");
        held.CreatedAtUtc = origin.AddDays(30);
        await ReplaceListingsAsync(factory, [.. listings, held]);

        using var response = await client.GetAsync(CatalogPath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Enumerable.Range(3, 12).Reverse().Select(index => $"Catalog item {index:D2}"),
            Cards(html).Select(CardTitle));
        Assert.DoesNotContain(held.Title, html);
    }

    private static Listing NewListing(ListingStatus status, string title) => new()
    {
        Title = title,
        Description = "Public catalog page test listing.",
        Condition = "Good",
        Size = "M",
        CategoryId = 1,
        Status = status,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static async Task ReplaceListingsAsync(AuthorizationWebApplicationFactory factory, params Listing[] listings)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await database.Listings.ExecuteDeleteAsync();
        database.Listings.AddRange(listings);
        await database.SaveChangesAsync();
    }

    private static string[] Cards(string html)
        => Regex.Matches(html, @"<article\b[^>]*>[\s\S]*?</article>", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => match.Value)
            .Where(article => Attribute(article, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("card"))
            .ToArray();

    private static string CardTitle(string card)
        => WebUtility.HtmlDecode(Assert.Single(Regex.Matches(card, @"<h[1-6]\b[^>]*>([\s\S]*?)</h[1-6]>",
            RegexOptions.IgnoreCase).Cast<Match>()).Groups[1].Value).Trim();

    private static string[] Tags(string html, string tag)
        => Regex.Matches(html, $@"<{tag}\b[^>]*>", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => match.Value).ToArray();

    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
}
