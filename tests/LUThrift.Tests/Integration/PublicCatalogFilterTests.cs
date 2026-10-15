using System.Net;
using System.Text.RegularExpressions;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LUThrift.Tests.Integration;

public class PublicCatalogFilterTests
{
    [Fact]
    public async Task CombinedFilters_ApplyTogetherAndKeepNormalizedSelections()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var matching = Listing("Reserved desk", ListingStatus.Held);
        await ReplaceListingsAsync(factory,
            matching,
            Listing("Available desk", ListingStatus.Available),
            Listing("Small desk", ListingStatus.Held, size: "S"),
            Listing("Different category desk", ListingStatus.Held, categoryId: 2),
            Listing("Reserved chair", ListingStatus.Held));

        using var response = await client.GetAsync(Query(
            ("Filter.Search", "  desk  "),
            ("Filter.CategoryId", "1"),
            ("Filter.Size", "  M  "),
            ("Filter.Availability", "1")));
        var main = MainContent(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { matching.Title }, CardTitles(main));
        Assert.Equal("desk", Attribute(Input(main, "Filter.Search"), "value"));
        Assert.Equal("1", SelectedValue(Select(main, "Filter.CategoryId")));
        Assert.Equal("M", SelectedValue(Select(main, "Filter.Size")));
        Assert.Equal("1", SelectedValue(Select(main, "Filter.Availability")));
        Assert.Contains("Reserved", Card(main));
        var form = Assert.Single(Regex.Matches(main, @"<form\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>()).Value;
        Assert.Equal("get", Attribute(form, "method"));
        Assert.Contains(Attribute(form, "action"), new[] { "/Thrift", "/Thrift/Index" });
    }

    [Fact]
    public async Task FilterChoices_UseDatabaseCategoriesAndOnlyPublicListingSizes()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var category = new Category { Name = "<b>Empty category</b>", DisplayOrder = 10 };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(category);
            await database.SaveChangesAsync();
        }
        var hidden = new[] { ListingStatus.Draft, ListingStatus.Missing, ListingStatus.Collected, ListingStatus.Archived }
            .Select(status => Listing($"Hidden {status}", status, size: $"Private-{status}")).ToArray();
        await ReplaceListingsAsync(factory,
            [Listing("Available item", ListingStatus.Available, size: "<em>M</em>"),
             Listing("Reserved item", ListingStatus.Held, size: "XL"), .. hidden]);

        using var response = await client.GetAsync("/Thrift");
        var main = MainContent(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var categories = Select(main, "Filter.CategoryId");
        Assert.Contains("Clothing", WebUtility.HtmlDecode(categories));
        Assert.Contains("Dorm supplies", WebUtility.HtmlDecode(categories));
        Assert.Contains("Books", WebUtility.HtmlDecode(categories));
        var categoryOption = Assert.Single(Options(categories), option => Attribute(option, "value") == category.Id.ToString());
        Assert.Contains(category.Name, WebUtility.HtmlDecode(categoryOption));
        Assert.DoesNotContain(category.Name, categoryOption);
        var sizes = Select(main, "Filter.Size");
        Assert.Equal(new[] { string.Empty, "<em>M</em>", "XL" },
            Options(sizes).Select(option => Attribute(option, "value")).OrderBy(value => value, StringComparer.Ordinal));
        Assert.DoesNotContain("<em>M</em>", sizes);
        Assert.Equal("0", SelectedValue(Select(main, "Filter.Availability")));
    }

    [Theory]
    [InlineData("0", false, true)]
    [InlineData("1", true, false)]
    [InlineData("2", true, true)]
    public async Task AvailabilityFilter_ShowsOnlyPermittedPublicStatuses(
        string availability, bool heldVisible, bool availableVisible)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        var listings = Enum.GetValues<ListingStatus>().Select(status => Listing($"Item {status}", status)).ToArray();
        await ReplaceListingsAsync(factory, listings);

        using var response = await client.GetAsync(Query(("Filter.Availability", availability)));
        var main = MainContent(await response.Content.ReadAsStringAsync());
        var titles = CardTitles(main);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(availability, SelectedValue(Select(main, "Filter.Availability")));
        foreach (var listing in listings)
        {
            var expected = listing.Status == ListingStatus.Available && availableVisible
                || listing.Status == ListingStatus.Held && heldVisible;
            Assert.Equal(expected, titles.Contains(listing.Title));
        }
        if (heldVisible)
        {
            var heldCard = Assert.Single(Cards(main), card => CardTitles(card).Single() == "Item Held");
            Assert.Matches(@">\s*Reserved\s*</span>", heldCard);
        }
    }

    public static IEnumerable<object[]> InvalidFilters =>
    [
        ["Filter.CategoryId", "not-a-number"],
        ["Filter.CategoryId", "0"],
        ["Filter.CategoryId", "-1"],
        ["Filter.CategoryId", "2147483648"],
        ["Filter.Availability", "not-an-option"],
        ["Filter.Availability", "999"],
        ["Filter.Search", new string('s', 151)],
        ["Filter.Size", new string('s', 51)],
        ["Filter.Search", "item\0text"],
        ["Filter.Size", "M\0"]
    ];

    [Theory]
    [MemberData(nameof(InvalidFilters))]
    public async Task InvalidFilters_ReturnBadRequestWithErrorsAndNoCards(string field, string value)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();

        using var response = await client.GetAsync(Query((field, value)));
        var main = MainContent(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("validation-summary-errors", main);
        Assert.Contains("field-validation-error", main);
        Assert.Contains("Check the filters and try again.", main);
        Assert.Empty(Cards(main));
        Assert.DoesNotContain("Blue hoodie", main);
        Assert.Contains("Clear filters", main);
    }

    [Theory]
    [InlineData("Filter.CategoryId", "2147483647")]
    [InlineData("Filter.Size", "Unknown size")]
    public async Task UnknownFilters_PreserveSelectionAndShowHelpfulEmptyState(string field, string value)
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();

        using var response = await client.GetAsync(Query((field, value)));
        var main = MainContent(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Cards(main));
        Assert.Contains("No items match these filters.", main);
        var selected = SelectedOption(Select(main, field));
        Assert.Equal(value, Attribute(selected, "value"));
        Assert.Contains(field == "Filter.CategoryId" ? "Unknown category" : value, WebUtility.HtmlDecode(selected));
        var clear = Assert.Single(Regex.Matches(main, @"<a\b[^>]*>\s*Clear filters\s*</a>",
            RegexOptions.IgnoreCase).Cast<Match>()).Value;
        var clearPath = Attribute(clear, "href");
        Assert.Contains(clearPath, new[] { "/Thrift", "/Thrift/Index" });
        using var clearedResponse = await client.GetAsync(clearPath);
        Assert.Equal(HttpStatusCode.OK, clearedResponse.StatusCode);
        Assert.NotEmpty(Cards(MainContent(await clearedResponse.Content.ReadAsStringAsync())));
    }

    [Fact]
    public async Task BlankFilters_NormalizeToDefaultChoicesAndKeepDefaultEmptyState()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        await ReplaceListingsAsync(factory, Listing("Reserved item", ListingStatus.Held));

        using var response = await client.GetAsync(Query(
            ("Filter.Search", " \t "), ("Filter.Size", " \t "), ("Filter.CategoryId", string.Empty)));
        var main = MainContent(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Cards(main));
        Assert.Equal(string.Empty, Attribute(Input(main, "Filter.Search"), "value"));
        Assert.Equal(string.Empty, SelectedValue(Select(main, "Filter.CategoryId")));
        Assert.Equal(string.Empty, SelectedValue(Select(main, "Filter.Size")));
        Assert.Equal("0", SelectedValue(Select(main, "Filter.Availability")));
        Assert.Contains("No items are available right now.", main);
        Assert.DoesNotContain("No items match these filters.", main);
    }

    [Fact]
    public async Task QueryText_IsEncodedInSearchAndUnknownSizeOption()
    {
        using var factory = new AuthorizationWebApplicationFactory();
        using var client = await factory.CreateIdentityClientAsync();
        const string search = "<script>alert('filter')</script>";
        const string size = "<em>Unknown</em>";

        using var response = await client.GetAsync(Query(("Filter.Search", search), ("Filter.Size", size)));
        var main = MainContent(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(search, Attribute(Input(main, "Filter.Search"), "value"));
        Assert.Equal(size, SelectedValue(Select(main, "Filter.Size")));
        Assert.DoesNotContain(search, main);
        Assert.DoesNotContain(size, main);
        Assert.DoesNotContain("<script", main, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Cards(main));
    }

    private static string Query(params (string Key, string Value)[] filters)
        => QueryHelpers.AddQueryString("/Thrift", filters.ToDictionary(filter => filter.Key, filter => (string?)filter.Value));

    private static Listing Listing(string title, ListingStatus status, int categoryId = 1, string? size = "M") => new()
    {
        Title = title,
        Description = "Catalog filter test description.",
        Condition = "Good",
        CategoryId = categoryId,
        Size = size,
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

    private static string MainContent(string html)
    {
        var match = Regex.Match(html, @"<main\b[^>]*>([\s\S]*?)</main>", RegexOptions.IgnoreCase);
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    private static string Input(string html, string name)
        => Assert.Single(Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase).Cast<Match>()
            .Select(match => match.Value), input => Attribute(input, "name") == name);

    private static string Select(string html, string name)
        => Assert.Single(Regex.Matches(html, @"<select\b[^>]*>[\s\S]*?</select>", RegexOptions.IgnoreCase).Cast<Match>()
            .Select(match => match.Value), select => Attribute(select, "name") == name);

    private static string[] Options(string select)
        => Regex.Matches(select, @"<option\b[^>]*>[\s\S]*?</option>", RegexOptions.IgnoreCase).Cast<Match>()
            .Select(match => match.Value).ToArray();

    private static string SelectedOption(string select)
    {
        var options = Options(select);
        var selected = options.Where(option => Regex.IsMatch(option, @"<option\b[^>]*\sselected(?:\s|=|>)",
            RegexOptions.IgnoreCase)).ToArray();
        return selected.Length == 0 ? options[0] : Assert.Single(selected);
    }

    private static string SelectedValue(string select) => Attribute(SelectedOption(select), "value");

    private static string[] Cards(string html)
        => Regex.Matches(html, @"<article\b[^>]*>[\s\S]*?</article>", RegexOptions.IgnoreCase).Cast<Match>()
            .Select(match => match.Value).ToArray();

    private static string Card(string html) => Assert.Single(Cards(html));

    private static string[] CardTitles(string html)
        => Cards(html).Select(card => WebUtility.HtmlDecode(Assert.Single(Regex.Matches(card,
            @"<h2\b[^>]*>([\s\S]*?)</h2>", RegexOptions.IgnoreCase).Cast<Match>()).Groups[1].Value).Trim()).ToArray();

    private static string Attribute(string html, string name)
        => WebUtility.HtmlDecode(Regex.Match(html, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value);
}
