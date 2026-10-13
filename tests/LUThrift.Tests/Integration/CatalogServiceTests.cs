using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests.Integration;

public class CatalogServiceTests
{
    [Fact]
    public async Task GetListings_ProjectsOnlyAvailableListingsWithoutTracking()
    {
        await using var test = await TestContext.CreateAsync();
        var listings = Enum.GetValues<ListingStatus>().Select(test.NewListing).ToArray();
        await test.SeedAsync(listings);
        var available = listings.Single(listing => listing.Status == ListingStatus.Available);

        var result = await test.Service.GetListingsAsync();

        Assert.Equal(new CatalogListingSummary(available.Id, available.Title, test.Category.Name,
            available.Condition, available.Size, ListingStatus.Available), Assert.Single(result));
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(ListingStatus.Draft, false)]
    [InlineData(ListingStatus.Available, true)]
    [InlineData(ListingStatus.Held, true)]
    [InlineData(ListingStatus.Collected, false)]
    [InlineData(ListingStatus.Missing, false)]
    [InlineData(ListingStatus.Archived, false)]
    public async Task GetDetails_EnforcesVisibilityAndProjectsPublicDetailsWithoutTracking(
        ListingStatus status, bool visible)
    {
        await using var test = await TestContext.CreateAsync();
        var listing = test.NewListing(status);
        listing.Size = status == ListingStatus.Held ? null : "M";
        await test.SeedAsync(listing);

        var result = await test.Service.GetDetailsAsync(listing.Id);

        if (visible)
        {
            Assert.Equal(new CatalogListingDetails(listing.Id, listing.Title, listing.Description,
                test.Category.Name, listing.Condition, listing.Size, status), result);
        }
        else
        {
            Assert.Null(result);
        }
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetDetails_UnknownIdReturnsNullWithoutTracking()
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(test.NewListing(ListingStatus.Available));

        var result = await test.Service.GetDetailsAsync(int.MaxValue);

        Assert.Null(result);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetListings_FiltersBeforeLimitingAndOrdersNewestFirstWithIdTieBreaker()
    {
        await using var test = await TestContext.CreateAsync();
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int[] dayOffsets = [1, 7, 2, 14, 6, 5, 12, 3, 11, 4, 10, 9, 20, 20];
        var available = dayOffsets.Select((days, index) =>
        {
            var listing = test.NewListing(ListingStatus.Available);
            listing.Id = 100 + index;
            listing.Title = $"Available listing {index}";
            listing.CreatedAtUtc = origin.AddDays(days);
            return listing;
        }).ToArray();
        var hidden = Enum.GetValues<ListingStatus>().Where(status => status != ListingStatus.Available)
            .Select((status, index) =>
            {
                var listing = test.NewListing(status);
                listing.Id = 200 + index;
                listing.CreatedAtUtc = origin.AddDays(30 + index);
                return listing;
            }).ToArray();
        await test.SeedAsync([.. available, .. hidden]);

        var result = await test.Service.GetListingsAsync();

        Assert.Equal(new[] { 113, 112, 103, 106, 108, 110, 111, 101, 104, 105, 109, 107 },
            result.Select(listing => listing.Id));
        Assert.All(result, listing => Assert.Equal(ListingStatus.Available, listing.Status));
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetListings_ReturnsEmptyWhenNoAvailableListingsExist(bool includeHiddenListings)
    {
        await using var test = await TestContext.CreateAsync();
        if (includeHiddenListings)
        {
            await test.SeedAsync(Enum.GetValues<ListingStatus>()
                .Where(status => status != ListingStatus.Available).Select(test.NewListing).ToArray());
        }

        var result = await test.Service.GetListingsAsync();

        Assert.Empty(result);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CatalogQueries_HonorCanceledRequests()
    {
        await using var test = await TestContext.CreateAsync();
        var listing = test.NewListing(ListingStatus.Available);
        await test.SeedAsync(listing);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.GetListingsAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.GetDetailsAsync(listing.Id, cancellation.Token));

        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    private sealed class TestContext(
        SqliteConnection connection, ApplicationDbContext database, Category category) : IAsyncDisposable
    {
        public ApplicationDbContext Database { get; } = database;
        public CatalogService Service { get; } = new(database);
        public Category Category { get; } = category;

        public static async Task<TestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var database = new ApplicationDbContext(options);
            await database.Database.EnsureCreatedAsync();
            await database.Listings.ExecuteDeleteAsync();
            var category = new Category { Name = "Catalog test category", DisplayOrder = 10 };
            database.Categories.Add(category);
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            return new TestContext(connection, database, category);
        }

        public Listing NewListing(ListingStatus status) => new()
        {
            Title = $"Catalog listing {status}",
            Description = $"Description for the {status} listing.",
            Condition = "Gently used",
            Size = "M",
            CategoryId = Category.Id,
            Status = status,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

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
