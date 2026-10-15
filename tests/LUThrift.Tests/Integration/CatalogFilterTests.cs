using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests.Integration;

public class CatalogFilterTests
{
    [Theory]
    [InlineData("hoodie")]
    [InlineData("HOODIE")]
    [InlineData(" \tHoOdIe  ")]
    public async Task Search_MatchesTitleSubstringIgnoringCaseAndOuterWhitespace(string search)
    {
        await using var test = await TestContext.CreateAsync();
        var upper = NewListing("Winter HOODIE with pockets");
        var lower = NewListing("Blue hoodie");
        var descriptionOnly = NewListing("Winter coat");
        descriptionOnly.Description = "A hoodie is mentioned only in this description.";
        await test.SeedAsync(upper, lower, descriptionOnly);

        var result = await test.Service.GetListingsAsync(new CatalogFilterInput { Search = search });

        AssertIds([upper, lower], result);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("!")]
    [InlineData("\\")]
    public async Task Search_TreatsPatternAndEscapeCharactersAsLiteralText(string search)
    {
        await using var test = await TestContext.CreateAsync();
        var matching = NewListing($"Special {search} item");
        await test.SeedAsync(matching, NewListing("Special plain item"), NewListing("Other item"));

        var result = await test.Service.GetListingsAsync(new CatalogFilterInput { Search = search });

        Assert.Equal(matching.Id, Assert.Single(result).Id);
    }

    [Theory]
    [InlineData(CatalogAvailability.Available)]
    [InlineData(CatalogAvailability.Reserved)]
    [InlineData(CatalogAvailability.All)]
    public async Task Availability_AlwaysExcludesPrivateListingStatuses(CatalogAvailability availability)
    {
        await using var test = await TestContext.CreateAsync();
        var listings = Enum.GetValues<ListingStatus>().Select(status => NewListing($"Item {status}", status)).ToArray();
        await test.SeedAsync(listings);

        var result = await test.Service.GetListingsAsync(new CatalogFilterInput { Availability = availability });

        ListingStatus[] expected = availability switch
        {
            CatalogAvailability.Available => [ListingStatus.Available],
            CatalogAvailability.Reserved => [ListingStatus.Held],
            _ => [ListingStatus.Available, ListingStatus.Held]
        };
        Assert.Equal(expected, result.Select(listing => listing.Status).OrderBy(status => status));
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CategoryAndSize_AreExactAndCombineWithSearchAndAvailability()
    {
        await using var test = await TestContext.CreateAsync();
        var available = NewListing("Blue hoodie");
        var held = NewListing("Reserved blue hoodie", ListingStatus.Held);
        var otherCategory = NewListing("Blue hoodie in another category");
        otherCategory.CategoryId = 2;
        var otherSize = NewListing("Blue hoodie in another size");
        otherSize.Size = "Medium";
        var otherTitle = NewListing("Winter coat");
        await test.SeedAsync(available, held, otherCategory, otherSize, otherTitle);

        var category = await test.Service.GetListingsAsync(new CatalogFilterInput
        {
            CategoryId = 1, Availability = CatalogAvailability.All
        });
        var size = await test.Service.GetListingsAsync(new CatalogFilterInput
        {
            Size = "M", Availability = CatalogAvailability.All
        });
        var combined = await test.Service.GetListingsAsync(new CatalogFilterInput
        {
            Search = " hoodie ", CategoryId = 1, Size = " M ", Availability = CatalogAvailability.All
        });
        var differentCaseSize = await test.Service.GetListingsAsync(new CatalogFilterInput { Size = "m" });

        AssertIds([available, held, otherSize, otherTitle], category);
        AssertIds([available, held, otherCategory, otherTitle], size);
        AssertIds([available, held], combined);
        Assert.Empty(differentCaseSize);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task BlankSearchAndSize_DoNotRestrictDefaultAvailableCatalog()
    {
        await using var test = await TestContext.CreateAsync();
        var sized = NewListing("Sized item");
        var noSize = NewListing("Item without a size");
        noSize.Size = null;
        await test.SeedAsync(sized, noSize, NewListing("Held item", ListingStatus.Held));

        var result = await test.Service.GetListingsAsync(new CatalogFilterInput { Search = " \t ", Size = " \t " });

        AssertIds([sized, noSize], result);
    }

    [Theory]
    [InlineData("LongSearch")]
    [InlineData("LongSize")]
    [InlineData("ZeroCategory")]
    [InlineData("NegativeCategory")]
    [InlineData("UndefinedAvailability")]
    [InlineData("NullCharacterSearch")]
    [InlineData("NullCharacterSize")]
    public async Task InvalidFilter_ReturnsEmptyWithoutThrowing(string invalidCase)
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(NewListing("Available item"));
        var filter = invalidCase switch
        {
            "LongSearch" => new CatalogFilterInput { Search = new string('s', 151) },
            "LongSize" => new CatalogFilterInput { Size = new string('s', 51) },
            "ZeroCategory" => new CatalogFilterInput { CategoryId = 0 },
            "NegativeCategory" => new CatalogFilterInput { CategoryId = -1 },
            "UndefinedAvailability" => new CatalogFilterInput { Availability = (CatalogAvailability)999 },
            "NullCharacterSearch" => new CatalogFilterInput { Search = "item\0text" },
            "NullCharacterSize" => new CatalogFilterInput { Size = "M\0" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };

        var result = await test.Service.GetListingsAsync(filter);

        Assert.Empty(result);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task UnknownSearchCategoryOrSize_ReturnsEmpty()
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(NewListing("Available hoodie"));
        CatalogFilterInput[] filters =
        [
            new() { Search = "Unmatched title" },
            new() { CategoryId = int.MaxValue },
            new() { Size = "Unknown size" }
        ];

        foreach (var filter in filters)
        {
            Assert.Empty(await test.Service.GetListingsAsync(filter));
        }
    }

    [Fact]
    public async Task AllFilters_RunBeforeLimitAndPreserveNewestFirstOrdering()
    {
        await using var test = await TestContext.CreateAsync();
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var matches = Enumerable.Range(1, 13).Select(index =>
        {
            var listing = NewListing($"Target item {index}", ListingStatus.Held);
            listing.Id = 100 + index;
            listing.CreatedAtUtc = origin.AddDays(index >= 12 ? 20 : index);
            return listing;
        }).ToArray();
        var distractors = Enumerable.Range(0, 16).Select(index =>
        {
            // Each group fails only one filter, so no filter can be deferred until after Take.
            var listing = NewListing(index < 4 ? "Other title" : "Target item", ListingStatus.Held);
            listing.Id = 200 + index;
            listing.CategoryId = index is >= 4 and < 8 ? 2 : 1;
            listing.Size = index is >= 8 and < 12 ? "Large" : "M";
            listing.Status = index >= 12 ? ListingStatus.Available : ListingStatus.Held;
            listing.CreatedAtUtc = origin.AddDays(30 + index);
            return listing;
        }).ToArray();
        await test.SeedAsync([.. matches, .. distractors]);

        var result = await test.Service.GetListingsAsync(new CatalogFilterInput
        {
            Search = "target", CategoryId = 1, Size = "M", Availability = CatalogAvailability.Reserved
        });

        Assert.Equal(new[] { 113, 112, 111, 110, 109, 108, 107, 106, 105, 104, 103, 102 },
            result.Select(listing => listing.Id));
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetCategories_ReturnsDatabaseOptionsOrderedByDisplayOrderThenName()
    {
        await using var test = await TestContext.CreateAsync();
        await test.Database.Categories.ExecuteDeleteAsync();
        test.Database.Categories.AddRange(
            new Category { Id = 11, Name = "Zulu", DisplayOrder = 2 },
            new Category { Id = 12, Name = "Bravo", DisplayOrder = 1 },
            new Category { Id = 13, Name = "Alpha", DisplayOrder = 1 });
        await test.Database.SaveChangesAsync();
        test.Database.ChangeTracker.Clear();

        var result = await test.Service.GetCategoriesAsync();

        Assert.Equal(new[]
        {
            new CatalogCategoryOption(13, "Alpha"),
            new CatalogCategoryOption(12, "Bravo"),
            new CatalogCategoryOption(11, "Zulu")
        }, result);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetSizes_ReturnsDistinctSortedNonblankSizesOnlyFromPublicListings()
    {
        await using var test = await TestContext.CreateAsync();
        var listings = new List<Listing>();
        foreach (var size in new string?[] { "M", "S", "M", null, string.Empty, "   " })
        {
            var listing = NewListing("Available size item");
            listing.Size = size;
            listings.Add(listing);
        }
        var held = NewListing("Held size item", ListingStatus.Held);
        held.Size = "L";
        listings.Add(held);
        foreach (var status in new[] { ListingStatus.Draft, ListingStatus.Collected, ListingStatus.Missing, ListingStatus.Archived })
        {
            var listing = NewListing("Private size item", status);
            listing.Size = $"Private {status} size";
            listings.Add(listing);
        }
        await test.SeedAsync(listings.ToArray());

        var result = await test.Service.GetSizesAsync();

        Assert.Equal(new[] { "L", "M", "S" }, result);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    private static void AssertIds(IEnumerable<Listing> expected, IEnumerable<CatalogListingSummary> actual)
        => Assert.Equal(expected.Select(listing => listing.Id).OrderBy(id => id), actual.Select(listing => listing.Id).OrderBy(id => id));

    private static Listing NewListing(string title, ListingStatus status = ListingStatus.Available) => new()
    {
        Title = title,
        Description = "Catalog filter test listing.",
        Condition = "Good",
        Size = "M",
        CategoryId = 1,
        Status = status,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class TestContext(SqliteConnection connection, ApplicationDbContext database) : IAsyncDisposable
    {
        public ApplicationDbContext Database { get; } = database;
        public CatalogService Service { get; } = new(database);

        public static async Task<TestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var database = new ApplicationDbContext(options);
            await database.Database.EnsureCreatedAsync();
            await database.Listings.ExecuteDeleteAsync();
            return new TestContext(connection, database);
        }

        public async Task SeedAsync(params Listing[] listings)
        {
            Database.Listings.AddRange(listings);
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
