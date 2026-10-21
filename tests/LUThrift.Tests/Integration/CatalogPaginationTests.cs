using System.Data.Common;
using System.Text.RegularExpressions;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LUThrift.Tests.Integration;

public class CatalogPaginationTests
{
    [Fact]
    public async Task Pages_CountFilteredResultsAndReturnStableNonoverlappingSlices()
    {
        var matching = MatchingListings();
        var otherTitle = NewListing(201, "Other item");
        var otherCategory = NewListing(202, "Target item in another category");
        otherCategory.CategoryId = 2;
        var otherSize = NewListing(203, "Target item in another size");
        otherSize.Size = "S";
        var otherStatuses = Enum.GetValues<ListingStatus>().Where(status => status != ListingStatus.Available)
            .Select((status, index) =>
            {
                var listing = NewListing(210 + index, $"Target item {status}");
                listing.Status = status;
                return listing;
            }).ToArray();
        await using var test = await TestContext.CreateAsync([.. matching, otherTitle, otherCategory, otherSize, .. otherStatuses]);
        var filter = new CatalogFilterInput
        {
            Search = "target", CategoryId = 1, Size = "M", Availability = CatalogAvailability.Available
        };

        var first = await test.Service.GetPageAsync(filter);
        var second = await test.Service.GetPageAsync(filter, 2);
        var third = await test.Service.GetPageAsync(filter, 3);
        var repeated = await test.Service.GetPageAsync(filter, 2);

        AssertPage(first, totalCount: 25, pageNumber: 1, itemCount: 12, previous: false, next: true);
        AssertPage(second, totalCount: 25, pageNumber: 2, itemCount: 12, previous: true, next: true);
        AssertPage(third, totalCount: 25, pageNumber: 3, itemCount: 1, previous: true, next: false);
        Assert.Equal(new[] { 120, 125, 124, 123, 122, 121, 119, 118, 117, 116, 115, 114 },
            first.Items.Select(listing => listing.Id));
        Assert.Equal(Enumerable.Range(102, 12).Reverse(), second.Items.Select(listing => listing.Id));
        Assert.Equal(101, Assert.Single(third.Items).Id);
        Assert.Equal(second.Items, repeated.Items);
        var allIds = first.Items.Concat(second.Items).Concat(third.Items).Select(listing => listing.Id).ToArray();
        Assert.Equal(25, allIds.Distinct().Count());
        Assert.Equal(matching.Select(listing => listing.Id).OrderBy(id => id), allIds.OrderBy(id => id));
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 3)]
    public async Task PageNumber_ClampsSafelyBeforeCalculatingOffset(int requestedPage, int expectedPage)
    {
        await using var test = await TestContext.CreateAsync(MatchingListings());

        var result = await test.Service.GetPageAsync(new CatalogFilterInput(), requestedPage);

        AssertPage(result, totalCount: 25, pageNumber: expectedPage, itemCount: expectedPage == 1 ? 12 : 1,
            previous: expectedPage > 1, next: expectedPage < 3);
        Assert.Equal(expectedPage == 1 ? 120 : 101, result.Items[0].Id);
    }

    [Fact]
    public async Task EmptyCatalog_HasOneEmptyPageEvenForExcessiveRequest()
    {
        await using var test = await TestContext.CreateAsync();

        var result = await test.Service.GetPageAsync(new CatalogFilterInput(), int.MaxValue);

        AssertEmptyPage(result);
    }

    [Fact]
    public async Task InvalidFilter_ReturnsOneEmptyPageWithoutExposingInventory()
    {
        await using var test = await TestContext.CreateAsync(MatchingListings());

        var result = await test.Service.GetPageAsync(
            new CatalogFilterInput { Availability = (CatalogAvailability)999 }, int.MaxValue);

        AssertEmptyPage(result);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetPage_HonorsCanceledRequest()
    {
        await using var test = await TestContext.CreateAsync(MatchingListings());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.GetPageAsync(new CatalogFilterInput(), 2, cancellation.Token));
    }

    [Fact]
    public async Task GetPage_CountsAndPaginatesInSql()
    {
        await using var test = await TestContext.CreateAsync(MatchingListings());

        var result = await test.Service.GetPageAsync(new CatalogFilterInput(), 2);

        Assert.Equal(25, result.TotalCount);
        Assert.Equal(12, result.Items.Count);
        Assert.Contains(test.Commands.Statements, sql => Regex.IsMatch(sql, @"\bCOUNT\s*\(", RegexOptions.IgnoreCase));
        Assert.Contains(test.Commands.Statements, sql =>
            Regex.IsMatch(sql, @"\bLIMIT\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(sql, @"\bOFFSET\b", RegexOptions.IgnoreCase));
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    private static void AssertPage(CatalogListingPage page, int totalCount, int pageNumber, int itemCount, bool previous, bool next)
    {
        Assert.Equal(totalCount, page.TotalCount);
        Assert.Equal(pageNumber, page.PageNumber);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(itemCount, page.Items.Count);
        Assert.Equal(previous, page.HasPreviousPage);
        Assert.Equal(next, page.HasNextPage);
    }

    private static void AssertEmptyPage(CatalogListingPage page)
    {
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasPreviousPage);
        Assert.False(page.HasNextPage);
    }

    private static Listing[] MatchingListings()
    {
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(1, 25).Select(index =>
        {
            var listing = NewListing(100 + index, $"Target item {index}");
            // IDs 114 and 113 share a timestamp on opposite sides of the first page boundary.
            var day = index == 20 ? 30 : index == 13 ? 14 : index;
            listing.CreatedAtUtc = origin.AddDays(day);
            return listing;
        }).ToArray();
    }

    private static Listing NewListing(int id, string title) => new()
    {
        Id = id,
        Title = title,
        Description = "Catalog pagination test listing.",
        Condition = "Good",
        Size = "M",
        CategoryId = 1,
        Status = ListingStatus.Available,
        CreatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Statements.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestContext(
        SqliteConnection connection, ApplicationDbContext database, CommandRecorder commands) : IAsyncDisposable
    {
        public ApplicationDbContext Database { get; } = database;
        public CatalogService Service { get; } = new(database);
        public CommandRecorder Commands { get; } = commands;

        public static async Task<TestContext> CreateAsync(params Listing[] listings)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var commands = new CommandRecorder();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).AddInterceptors(commands).Options;
            var database = new ApplicationDbContext(options);
            await database.Database.EnsureCreatedAsync();
            await database.Listings.ExecuteDeleteAsync();
            database.Listings.AddRange(listings);
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            commands.Statements.Clear();
            return new TestContext(connection, database, commands);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
