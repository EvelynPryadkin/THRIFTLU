using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LUThrift.Tests.Integration;

public class CatalogPostgresTests
{
    internal const string ConnectionEnvironmentVariable = "LUTHRIFT_CATALOG_TEST_CONNECTION";
    private const int CategoryId = 10001;
    private const int OtherCategoryId = 10002;

    [CatalogPostgresFact]
    public async Task Filters_ExecuteWithPostgresCaseMatchingLiteralSearchAndPublicVisibility()
    {
        // This deliberately has no application-configuration fallback. Only an explicitly supplied
        // loopback connection to the designated test database can reach EnsureCreated or any writes.
        var connection = TestConnection();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var database = new ApplicationDbContext(options);
        await database.Database.EnsureCreatedAsync();
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.Categories.AddRange(
            new Category { Id = CategoryId, Name = "PostgreSQL catalog test", DisplayOrder = 100 },
            new Category { Id = OtherCategoryId, Name = "Other PostgreSQL catalog test", DisplayOrder = 101 });
        var privateSize = $"Private-{Guid.NewGuid():N}";
        Listing[] listings =
        [
            Listing(5001, "MiXeD Wool Cardigan", "Soft winter knit."),
            Listing(5002, "Everyday cardigan", "COZY MÄNTEL lining.", size: "L"),
            Listing(5003, "MÄNTEL coat", "Winter outerwear."),
            Listing(5004, "100% cotton", "A literal percent character."),
            Listing(5005, "100x cotton", "A percent wildcard must not match this control."),
            Listing(5006, "code_name bag", "A literal underscore character."),
            Listing(5007, "codeXname bag", "An underscore wildcard must not match this control."),
            Listing(5008, "Ready! coat", "A literal escape character."),
            Listing(5009, @"C:\books shelf", "A literal backslash character."),
            Listing(5010, "Reserved cardigan", "Held inventory.", ListingStatus.Held),
            Listing(5011, "Other cardigan", "Another category.", categoryId: OtherCategoryId),
            Listing(5012, "Small cardigan", "Another size.", size: "S"),
            Listing(5013, "Hidden cardigan draft", "Private draft.", ListingStatus.Draft, size: privateSize),
            Listing(5014, "Hidden cardigan missing", "Private missing item.", ListingStatus.Missing),
            Listing(5015, "Hidden cardigan collected", "Private collected item.", ListingStatus.Collected),
            Listing(5016, "Hidden cardigan archived", "Private archived item.", ListingStatus.Archived)
        ];
        database.Listings.AddRange(listings);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var before = await SnapshotAsync(database);
        var service = new CatalogService(database);

        (string Search, int[] ExpectedIds)[] searches =
        [
            ("mixed WOOL", [5001]),
            ("everyday CARDIGAN", [5002]),
            ("soft WINTER", []),
            ("mäntel", [5003]),
            ("cozy mäntel", []),
            ("%", [5004]),
            ("_", [5006]),
            ("!", [5008]),
            (@"\", [5009])
        ];
        foreach (var (search, expectedIds) in searches)
        {
            var result = await service.GetListingsAsync(new CatalogFilterInput
            {
                Search = search, CategoryId = CategoryId
            });
            Assert.Equal(expectedIds, result.Select(listing => listing.Id).Order());
        }

        var combined = await service.GetListingsAsync(new CatalogFilterInput
        {
            Search = "  CARDIGAN  ", CategoryId = CategoryId, Size = " M ",
            Availability = CatalogAvailability.All
        });
        Assert.Equal(new[] { 5001, 5010 }, combined.Select(listing => listing.Id).Order());

        foreach (var availability in Enum.GetValues<CatalogAvailability>())
        {
            var result = await service.GetListingsAsync(new CatalogFilterInput
            {
                CategoryId = CategoryId, Availability = availability
            });
            var expected = listings.Where(listing => listing.CategoryId == CategoryId && (availability switch
            {
                CatalogAvailability.Available => listing.Status == ListingStatus.Available,
                CatalogAvailability.Reserved => listing.Status == ListingStatus.Held,
                CatalogAvailability.All => listing.Status is ListingStatus.Available or ListingStatus.Held,
                _ => false
            }));
            Assert.Equal(expected.Select(listing => listing.Id).Order(), result.Select(listing => listing.Id).Order());

            var hidden = await service.GetListingsAsync(new CatalogFilterInput
            {
                Search = "hidden", CategoryId = CategoryId, Availability = availability
            });
            Assert.Empty(hidden);
        }

        var categories = await service.GetCategoriesAsync();
        Assert.Contains(categories, category => category.Id == CategoryId && category.Name == "PostgreSQL catalog test");
        Assert.Contains(categories, category => category.Id == OtherCategoryId && category.Name == "Other PostgreSQL catalog test");
        var sizes = await service.GetSizesAsync();
        Assert.All(new[] { "L", "M", "S" }, size => Assert.Contains(size, sizes));
        Assert.DoesNotContain(privateSize, sizes);

        var after = await SnapshotAsync(database);
        Assert.Equal(before.Listings, after.Listings);
        Assert.Equal(before.Audits, after.Audits);
        Assert.Empty(database.ChangeTracker.Entries());
        // Leave no test rows behind if the caller reuses its dedicated test database.
        await transaction.RollbackAsync();
    }

    [CatalogPostgresFact]
    public async Task Pagination_CountsFilteredMatchesAndKeepsTiedPagesDisjointWithoutWritingInventoryOrAudits()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(TestConnection()).Options;
        await using var database = new ApplicationDbContext(options);
        await database.Database.EnsureCreatedAsync();
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.Categories.AddRange(
            new Category { Id = CategoryId, Name = "PostgreSQL pagination test", DisplayOrder = 100 },
            new Category { Id = OtherCategoryId, Name = "Other PostgreSQL pagination test", DisplayOrder = 101 });
        const string title = @"MÄNTEL 100%_!\Coat";
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var matching = Enumerable.Range(0, 25).Select(index =>
        {
            var listing = Listing(20000 + index, $"{title} {index}", "A matching catalog item.",
                index % 2 == 0 ? ListingStatus.Available : ListingStatus.Held);
            // A tied date group straddles a page boundary, so the ID tie-breaker matters.
            listing.CreatedAtUtc = origin.AddDays(index / 3);
            return listing;
        }).ToArray();
        Listing[] excluded =
        [
            Listing(21000, title, "Private draft.", ListingStatus.Draft),
            Listing(21001, title, "Private missing item.", ListingStatus.Missing),
            Listing(21002, title, "Private collected item.", ListingStatus.Collected),
            Listing(21003, title, "Private archived item.", ListingStatus.Archived),
            Listing(21004, title, "Another category.", categoryId: OtherCategoryId),
            Listing(21005, title, "Another size.", size: "S"),
            Listing(21006, @"MÄNTEL 100anything_!\Coat", "Percent must be literal."),
            Listing(21007, @"MÄNTEL 100%X!\Coat", "Underscore must be literal."),
            Listing(21008, @"MÄNTEL 100%_!Coat", "Backslash must be literal."),
            Listing(21009, "Description-only control", title)
        ];
        foreach (var listing in excluded)
        {
            listing.CreatedAtUtc = origin.AddYears(1);
        }
        database.Listings.AddRange(matching.Concat(excluded));
        database.AuditEvents.Add(new AuditEvent
        {
            Id = 7001, ListingId = matching[0].Id, ActorUserId = "postgres-catalog-test-actor",
            Action = InventoryAuditActions.Created, OccurredAtUtc = origin,
            Details = "Existing audit history must remain unchanged during catalog reads."
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var before = await SnapshotAsync(database);
        var service = new CatalogService(database);
        var filter = new CatalogFilterInput
        {
            Search = @"  mäntel 100%_!\COAT  ", CategoryId = CategoryId, Size = " M ",
            Availability = CatalogAvailability.All
        };
        var expectedOrder = matching.OrderByDescending(listing => listing.CreatedAtUtc)
            .ThenByDescending(listing => listing.Id).Select(listing => listing.Id).ToArray();
        var seenIds = new List<int>();
        int[] expectedPageSizes = [12, 12, 1];

        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = await service.GetPageAsync(filter, pageNumber);

            Assert.Equal(25, page.TotalCount);
            Assert.Equal(3, page.TotalPages);
            Assert.Equal(pageNumber, page.PageNumber);
            Assert.Equal(pageNumber > 1, page.HasPreviousPage);
            Assert.Equal(pageNumber < 3, page.HasNextPage);
            Assert.Equal(expectedPageSizes[pageNumber - 1], page.Items.Count);
            var ids = page.Items.Select(listing => listing.Id).ToArray();
            Assert.Equal(expectedOrder.Skip((pageNumber - 1) * 12).Take(12), ids);
            Assert.Empty(ids.Intersect(seenIds));
            seenIds.AddRange(ids);
            Assert.All(page.Items, listing => Assert.True(
                listing.Status is ListingStatus.Available or ListingStatus.Held));
        }

        Assert.Equal(expectedOrder, seenIds);
        var repeated = await service.GetPageAsync(filter, 2);
        Assert.Equal(expectedOrder.Skip(12).Take(12), repeated.Items.Select(listing => listing.Id));
        var beyondLast = await service.GetPageAsync(filter, int.MaxValue);
        Assert.Equal(3, beyondLast.PageNumber);
        Assert.Equal(25, beyondLast.TotalCount);
        Assert.Equal(expectedOrder[24], Assert.Single(beyondLast.Items).Id);

        filter.Availability = CatalogAvailability.Reserved;
        var reserved = await service.GetPageAsync(filter);
        Assert.Equal(12, reserved.TotalCount);
        Assert.Equal(12, reserved.Items.Count);
        Assert.All(reserved.Items, listing => Assert.Equal(ListingStatus.Held, listing.Status));

        var after = await SnapshotAsync(database);
        Assert.Equal(before.Listings, after.Listings);
        Assert.Equal(before.Audits, after.Audits);
        Assert.Empty(database.ChangeTracker.Entries());
        await transaction.RollbackAsync();
    }

    private static async Task<(object[] Listings, object[] Audits)> SnapshotAsync(ApplicationDbContext database)
    {
        var listings = await database.Listings.AsNoTracking().OrderBy(listing => listing.Id).ToArrayAsync();
        var audits = await database.AuditEvents.AsNoTracking().OrderBy(audit => audit.Id).ToArrayAsync();
        return (
            listings.Select(listing => (object)(listing.Id, listing.Title, listing.Description, listing.Condition,
                listing.Size, listing.CategoryId, listing.Status, listing.CreatedAtUtc)).ToArray(),
            audits.Select(audit => (object)(audit.Id, audit.ListingId, audit.ActorUserId, audit.Action,
                audit.OccurredAtUtc, audit.Details)).ToArray());
    }

    private static string TestConnection()
    {
        NpgsqlConnectionStringBuilder connection;
        try
        {
            connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable));
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The catalog PostgreSQL test connection is invalid.");
        }

        if (connection.Host is not ("localhost" or "127.0.0.1" or "::1")
            || connection.Database != "luthrift_catalog_test")
        {
            throw new InvalidOperationException(
                "Catalog PostgreSQL tests require a loopback host and the dedicated luthrift_catalog_test database.");
        }

        return connection.ConnectionString;
    }

    private static Listing Listing(int id, string title, string description,
        ListingStatus status = ListingStatus.Available, string size = "M", int categoryId = CategoryId) => new()
    {
        Id = id, Title = title, Description = description, Condition = "Good", Size = size,
        CategoryId = categoryId, Status = status,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(id)
    };
}

public sealed class CatalogPostgresFactAttribute : FactAttribute
{
    public CatalogPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CatalogPostgresTests.ConnectionEnvironmentVariable)))
        {
            Skip = "Set LUTHRIFT_CATALOG_TEST_CONNECTION to run against a disposable PostgreSQL test database.";
        }
    }
}
