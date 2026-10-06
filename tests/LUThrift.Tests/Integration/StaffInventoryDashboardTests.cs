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

public class StaffInventoryDashboardTests
{
    private const string DashboardPath = "/Staff/Inventory";

    [Theory]
    [InlineData(null, "/Identity/Account/Login")]
    [InlineData(ApplicationRoles.Student, "/Identity/Account/AccessDenied")]
    [InlineData(ApplicationRoles.Staff, null)]
    [InlineData(ApplicationRoles.Administrator, null)]
    public async Task Dashboard_EnforcesStaffAccessAndStaffLandingLinksToIt(string? role, string? redirectPath)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(role);

        using var response = await client.GetAsync(DashboardPath);

        if (redirectPath is not null)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            var location = new Uri(client.BaseAddress!, response.Headers.Location);
            Assert.Equal(redirectPath, location.AbsolutePath);
            Assert.Equal(DashboardPath, QueryHelpers.ParseQuery(location.Query)["ReturnUrl"].ToString());
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var landing = await client.GetAsync("/Staff");
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        Assert.Contains(Tags(await landing.Content.ReadAsStringAsync(), "a"),
            link => Attribute(link, "href") == DashboardPath);
    }

    [Fact]
    public async Task Dashboard_RendersAllStatusesInNewestOrderWithMetadataAndDisabledFutureLinks()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var listings = await ReplaceInventoryAsync(factory);

        using var response = await client.GetAsync(DashboardPath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = TableRows(html);
        Assert.Equal(6, rows.Length);
        ListingStatus[] expectedOrder =
        [
            ListingStatus.Held, ListingStatus.Draft, ListingStatus.Collected,
            ListingStatus.Archived, ListingStatus.Available, ListingStatus.Missing
        ];
        var renderedListings = rows.Select(row => Assert.Single(listings,
            listing => row.Contains(listing.Title, StringComparison.Ordinal))).ToArray();
        Assert.Equal(expectedOrder, renderedListings.Select(listing => listing.Status));
        var placeholderPath = Attribute(Assert.Single(Tags(rows[0], "img")), "src");
        // ASP.NET may fingerprint static asset URLs; follow the local URL rendered by the page.
        Assert.Matches(@"^/images/listing-placeholder(?:\.[A-Za-z0-9_-]+)?\.svg$", placeholderPath);

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var listing = renderedListings[index];
            Assert.Contains(listing.Category.Name, row);
            Assert.Contains(listing.Condition, row);
            Assert.Contains(listing.Size ?? "Not specified", row);
            Assert.Contains(listing.Status.ToString(), row);
            var time = Regex.Match(row, @"<time\b[^>]*>(?<text>[\s\S]*?)</time>", RegexOptions.IgnoreCase);
            Assert.True(time.Success, "Expected a machine-readable UTC creation time.");
            var timestamp = DateTimeOffset.Parse(Attribute(time.Value, "datetime"), CultureInfo.InvariantCulture);
            Assert.Equal(TimeSpan.Zero, timestamp.Offset);
            Assert.Equal(listing.CreatedAtUtc, timestamp.UtcDateTime);
            Assert.Equal(listing.CreatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                WebUtility.HtmlDecode(time.Groups["text"].Value).Trim());
            var image = Assert.Single(Tags(row, "img"));
            Assert.Equal(placeholderPath, Attribute(image, "src"));
            Assert.Equal("No image available", Attribute(image, "alt"));

            var editLinks = Tags(row, "a")
                .Where(link => Attribute(link, "href").StartsWith("/Staff/Inventory/Edit", StringComparison.Ordinal))
                .ToArray();
            if (listing.Status is ListingStatus.Draft or ListingStatus.Available or ListingStatus.Missing)
            {
                var link = Assert.Single(editLinks);
                AssertDisabled(link);
                var target = new Uri(client.BaseAddress!, Attribute(link, "href"));
                Assert.Equal("/Staff/Inventory/Edit", target.AbsolutePath);
                Assert.Equal(listing.Id.ToString(CultureInfo.InvariantCulture), QueryHelpers.ParseQuery(target.Query)["id"].ToString());
            }
            else
            {
                Assert.Empty(editLinks);
            }
        }

        var createLink = Assert.Single(Tags(html, "a"), link => Attribute(link, "href") == "/Staff/Inventory/Create");
        AssertDisabled(createLink);
        using var placeholder = await client.GetAsync(placeholderPath);
        Assert.Equal(HttpStatusCode.OK, placeholder.StatusCode);
        Assert.Equal("image/svg+xml", placeholder.Content.Headers.ContentType?.MediaType);
        var filter = Assert.Single(Regex.Matches(html, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase)
            .Cast<Match>(), form => form.Value.Contains("name=\"Status\"", StringComparison.Ordinal));
        Assert.Equal("get", Attribute(filter.Value, "method"), ignoreCase: true);
        var selector = Assert.Single(Regex.Matches(filter.Value, @"<select\b[^>]*>[\s\S]*?</select>", RegexOptions.IgnoreCase)
            .Cast<Match>(), select => Attribute(select.Value, "name") == "Status");
        var statusOptions = Tags(selector.Value, "option").Select(option => Attribute(option, "value"))
            .Where(value => value.Length > 0).Select(Enum.Parse<ListingStatus>).OrderBy(status => status);
        Assert.Equal(Enum.GetValues<ListingStatus>().OrderBy(status => status), statusOptions);
    }

    [Fact]
    public async Task Dashboard_StatusFilterShowsOnlyMatchingListings()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        var listings = await ReplaceInventoryAsync(factory);

        using var response = await client.GetAsync(DashboardPath + "?Status=Held");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(TableRows(html));
        Assert.Contains(Assert.Single(listings, listing => listing.Status == ListingStatus.Held).Title, row);
        foreach (var excluded in listings.Where(listing => listing.Status != ListingStatus.Held))
        {
            Assert.DoesNotContain(excluded.Title, html);
        }
    }

    [Fact]
    public async Task Dashboard_UnmatchedStatusExplainsFilteredEmptyState()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        await ReplaceInventoryAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Listings
                .Where(listing => listing.Status == ListingStatus.Held).ExecuteDeleteAsync();
        }

        using var response = await client.GetAsync(DashboardPath + "?Status=Held");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No listings match this status.", html);
        Assert.DoesNotContain("No inventory listings yet.", html);
        Assert.Empty(TableRows(html));
    }

    [Fact]
    public async Task Dashboard_EmptyInventoryExplainsEmptyState()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Listings.ExecuteDeleteAsync();
        }

        using var response = await client.GetAsync(DashboardPath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No inventory listings yet.", html);
        Assert.DoesNotContain("No listings match this status.", html);
        Assert.Empty(TableRows(html));
    }

    [Theory]
    [InlineData("not-a-status")]
    [InlineData("999")]
    public async Task Dashboard_InvalidStatusReturnsSafeBadRequest(string status)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync(ApplicationRoles.Staff);

        using var response = await client.GetAsync(QueryHelpers.AddQueryString(DashboardPath, "Status", status));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Choose a valid inventory status.", body);
        Assert.DoesNotContain(status, body);
    }

    private static async Task<IReadOnlyList<Listing>> ReplaceInventoryAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await database.Listings.ExecuteDeleteAsync();
        var category = await database.Categories.OrderBy(category => category.Id).FirstAsync();
        var createdAt = new DateTime(2026, 1, 1, 14, 30, 0, DateTimeKind.Utc);
        int[] dayOffsets = [3, 1, 3, 2, 0, 1];
        var listings = Enum.GetValues<ListingStatus>().Select((status, index) => new Listing
        {
            Title = $"Inventory item {index + 1}",
            Description = $"Inventory description {index + 1}",
            Condition = $"Condition {index + 1}",
            Size = index == 0 ? null : $"Size {index + 1}",
            Category = category,
            CategoryId = category.Id,
            Status = status,
            CreatedAtUtc = createdAt.AddDays(dayOffsets[index])
        }).ToArray();
        database.Listings.AddRange(listings);
        await database.SaveChangesAsync();
        return listings;
    }

    private static string[] TableRows(string html)
    {
        var body = Regex.Match(html, @"<tbody\b[^>]*>([\s\S]*?)</tbody>", RegexOptions.IgnoreCase).Groups[1].Value;
        return Regex.Matches(body, @"<tr\b[^>]*>[\s\S]*?</tr>", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => match.Value).ToArray();
    }

    private static string[] Tags(string html, string tag)
        => Regex.Matches(html, $@"<{tag}\b[^>]*>", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => match.Value).ToArray();

    private static string Attribute(string tag, string attribute)
        => WebUtility.HtmlDecode(Regex.Match(tag, $"\\b{attribute}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);

    private static void AssertDisabled(string link)
    {
        Assert.Equal("true", Attribute(link, "aria-disabled"));
        Assert.Contains("disabled", Attribute(link, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
