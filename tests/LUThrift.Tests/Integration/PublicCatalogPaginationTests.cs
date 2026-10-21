using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public class PublicCatalogPaginationTests
{
    [Fact]
    public async Task Pages_PreserveFiltersAndDetailReturnContext_WhileNewFiltersResetThePage()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        await SeedAsync(factory);
        var filters = new Dictionary<string, string?>
        {
            ["Filter.Search"] = "100%_!", ["Filter.CategoryId"] = "1",
            ["Filter.Size"] = "M & tall", ["Filter.Availability"] = "1"
        };

        var first = await GetHtmlAsync(client, QueryHelpers.AddQueryString("/Thrift", filters));
        AssertSummary(first, 25, 1, 3);
        AssertDisabled(first, "Previous");
        var next = Link(first, "Next");
        AssertQuery(next, filters, 2);

        var second = await GetHtmlAsync(client, next);
        AssertSummary(second, 25, 2, 3);
        var previous = Link(second, "Previous");
        AssertQuery(previous, filters, 1);
        Assert.Equal(CardIds(first), CardIds(await GetHtmlAsync(client, previous)));
        AssertQuery(Link(second, "Next"), filters, 3);

        var third = await GetHtmlAsync(client, Link(second, "Next"));
        AssertSummary(third, 25, 3, 3);
        AssertDisabled(third, "Next");
        AssertQuery(Link(third, "Previous"), filters, 2);
        Assert.Equal(new[] { 12, 12, 1 }, new[] { first, second, third }.Select(html => CardIds(html).Length));
        var allIds = new[] { first, second, third }.SelectMany(CardIds).ToArray();
        Assert.Equal(Enumerable.Range(101, 25).Reverse(), allIds);
        Assert.Equal(25, allIds.Distinct().Count());

        var detailsLink = Attribute(Assert.Single(Tags(Cards(second)[0], "a")), "href");
        AssertQuery(detailsLink, filters, 2);
        var details = await GetHtmlAsync(client, detailsLink);
        var back = Link(details, "Back to catalog");
        AssertQuery(back, filters, 2);
        var returned = await GetHtmlAsync(client, back);
        Assert.Equal(CardIds(second), CardIds(returned));
        AssertSummary(returned, 25, 2, 3);

        // Submit the actual rendered controls: the form must not carry the old page number.
        var form = Assert.Single(Regex.Matches(returned, @"<form\b[^>]*>[\s\S]*?</form>", RegexOptions.IgnoreCase).Cast<Match>()).Value;
        Assert.Equal("get", Attribute(form, "method"));
        Assert.DoesNotContain("?", Attribute(form, "action"));
        var submitted = FormValues(form);
        Assert.False(submitted.ContainsKey("pageNumber"));
        foreach (var (key, value) in filters)
        {
            Assert.Equal(value, submitted[key]);
        }
        submitted["Filter.Search"] = "Needle";
        var reset = await GetHtmlAsync(client, QueryHelpers.AddQueryString(Attribute(form, "action"), submitted));
        AssertSummary(reset, 25, 1, 3);
        Assert.Equal(CardIds(first), CardIds(reset));

        var clear = Link(returned, "Clear filters");
        Assert.DoesNotContain("?", clear);
    }

    [Theory]
    [InlineData("", 1, 12)]
    [InlineData("not-a-page", 1, 12)]
    [InlineData("1.5", 1, 12)]
    [InlineData("0", 1, 12)]
    [InlineData("-2", 1, 12)]
    [InlineData("-2147483648", 1, 12)]
    [InlineData("2147483648", 1, 12)]
    [InlineData("999999999999999999999999999999", 1, 12)]
    [InlineData("2", 2, 12)]
    [InlineData("99", 3, 1)]
    [InlineData("2147483647", 3, 1)]
    public async Task PageBoundaries_AreSafeAndPageSizeCannotBeSubmitted(string pageNumber, int expectedPage, int expectedItems)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        await SeedAsync(factory);
        var url = QueryHelpers.AddQueryString("/Thrift", new Dictionary<string, string?>
        {
            ["Filter.Availability"] = "1", ["Filter.Search"] = "100%_!",
            ["Filter.CategoryId"] = "1", ["Filter.Size"] = "M & tall",
            ["pageNumber"] = pageNumber, ["pageSize"] = "1000"
        });

        var html = await GetHtmlAsync(client, url);

        AssertSummary(html, 25, expectedPage, 3);
        Assert.Equal(expectedItems, CardIds(html).Length);
        Assert.Equal(Enumerable.Range(101, 25).Reverse().Skip((expectedPage - 1) * 12).Take(12), CardIds(html));
    }

    [Fact]
    public async Task NoMatches_UsePageOneWithNoActivePaginationLinks()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();

        var html = await GetHtmlAsync(client, "/Thrift?Filter.Search=nonexistent&pageNumber=2147483647");

        AssertSummary(html, 0, 1, 1);
        Assert.Contains("No items match these filters.", html);
        Assert.Empty(CardIds(html));
        AssertDisabled(html, "Previous");
        AssertDisabled(html, "Next");
    }

    [Fact]
    public async Task Details_IgnoreInvalidNavigationContextAndKeepReturnLinkLocal()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var url = QueryHelpers.AddQueryString("/Thrift/Details/1", new Dictionary<string, string?>
        {
            ["Filter.Availability"] = "not-an-option", ["pageNumber"] = "bad",
            ["returnUrl"] = "https://example.invalid/"
        });

        var html = await GetHtmlAsync(client, url);

        Assert.Contains("Blue hoodie", html);
        Assert.Contains(Link(html, "Back to catalog"), new[] { "/Thrift", "/Thrift/Index" });
    }

    private static async Task SeedAsync(AuthorizationWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await database.Listings.ExecuteDeleteAsync();
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Listing NewListing(int id) => new()
        {
            Id = id, Title = $"Needle 100%_! item {id}", Description = "Pagination test item.",
            CategoryId = 1, Condition = "Good", Size = "M & tall", Status = ListingStatus.Held,
            CreatedAtUtc = timestamp
        };
        database.Listings.AddRange(Enumerable.Range(101, 25).Select(NewListing));
        var otherCategory = NewListing(201);
        otherCategory.CategoryId = 2;
        var otherSize = NewListing(202);
        otherSize.Size = "S";
        var otherTitle = NewListing(203);
        otherTitle.Title = "Unrelated item";
        database.Listings.AddRange(otherCategory, otherSize, otherTitle);
        foreach (var status in Enum.GetValues<ListingStatus>().Where(status => status != ListingStatus.Held))
        {
            var excluded = NewListing(300 + (int)status);
            excluded.Status = status;
            excluded.CreatedAtUtc = timestamp.AddDays(1);
            database.Listings.Add(excluded);
        }
        await database.SaveChangesAsync();
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertSummary(string html, int count, int page, int totalPages)
    {
        var summary = Assert.Single(Regex.Matches(html, @"<p\b[^>]*id=""catalog-summary""[^>]*>([\s\S]*?)</p>").Cast<Match>());
        Assert.Equal($"{count} {(count == 1 ? "item" : "items")} found. Page {page} of {totalPages}.",
            WebUtility.HtmlDecode(summary.Groups[1].Value).Trim());
        Assert.Matches($@"aria-current=""page"">Page {page} of {totalPages}</span>", html);
        Assert.Contains("aria-label=\"Catalog pagination\"", html);
    }

    private static void AssertQuery(string url, Dictionary<string, string?> filters, int page)
    {
        var query = QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), url).Query);
        foreach (var (key, value) in filters)
        {
            Assert.True(query.ContainsKey(key), $"Missing {key} in {url}");
            Assert.Equal(value, query[key].ToString());
        }
        Assert.Equal(page, query.TryGetValue("pageNumber", out var actualPage) ? int.Parse(actualPage.ToString()) : 1);
    }

    private static void AssertDisabled(string html, string label)
    {
        Assert.Matches($@"<span\b[^>]*aria-disabled=""true""[^>]*>{label}</span>", html);
        Assert.DoesNotContain(Tags(html, "a"), link => Regex.IsMatch(link, $@">\s*{label}\s*</a>"));
    }

    private static Dictionary<string, string?> FormValues(string form)
    {
        var values = Tags(form, "input").ToDictionary(input => Attribute(input, "name"), input => (string?)Attribute(input, "value"));
        foreach (var select in Tags(form, "select"))
        {
            var options = Tags(select, "option");
            var selected = options.SingleOrDefault(option => Regex.IsMatch(option, @"\sselected(?:\s|=|>)")) ?? options[0];
            values[Attribute(select, "name")] = Attribute(selected, "value");
        }
        return values;
    }

    private static string[] Tags(string html, string tag)
        => Regex.Matches(html, tag == "input" ? @"<input\b[^>]*>" : $@"<{tag}\b[^>]*>[\s\S]*?</{tag}>", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(match => match.Value).ToArray();

    private static string Link(string html, string text)
        => Attribute(Assert.Single(Tags(html, "a"), link => Regex.IsMatch(link, $@">\s*{Regex.Escape(text)}\s*</a>")), "href");

    private static string[] Cards(string html) => Tags(html, "article");

    private static int[] CardIds(string html)
        => Cards(html).Select(card => int.Parse(Attribute(card, "aria-labelledby").Replace("listing-title-", ""))).ToArray();

    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
}
