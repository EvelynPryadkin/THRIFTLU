using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests.Integration;

public class ListingServiceTests
{
    [Fact]
    public async Task Create_TrimsInputAndPersistsDraftWithAudit()
    {
        await using var test = await TestContext.CreateAsync();
        var input = NewInput(test.Original.CategoryId);
        input.Size = " \t ";
        var startedAt = DateTime.UtcNow;

        var result = await test.Service.CreateAsync(input, test.ActorId);

        Assert.True(result.Succeeded);
        Assert.Equal(ListingResultStatus.Success, result.Status);
        Assert.Empty(result.Errors);
        var listing = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == result.Value);
        Assert.Equal("Service listing", listing.Title);
        Assert.Equal("Service description", listing.Description);
        Assert.Equal("Good", listing.Condition);
        Assert.Null(listing.Size);
        Assert.Equal(input.CategoryId, listing.CategoryId);
        Assert.Equal(ListingStatus.Draft, listing.Status);
        Assert.InRange(listing.CreatedAtUtc, startedAt, DateTime.UtcNow);
        var audit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(listing.Id, audit.ListingId);
        Assert.Equal(test.ActorId, audit.ActorUserId);
        Assert.Equal(InventoryAuditActions.Created, audit.Action);
        Assert.InRange(audit.OccurredAtUtc, startedAt, DateTime.UtcNow);
    }

    public static IEnumerable<object?[]> InvalidInputs =>
    [
        [null, null, ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.Title), " \t ", ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.Description), null, ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.Condition), "   ", ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.Title), new string('t', 151), ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.Description), new string('d', 2001), ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.Condition), new string('c', 101), ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.Size), new string('s', 51), ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.CategoryId), 0, ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.CategoryId), -1, ListingResultStatus.InvalidInput],
        [nameof(CreateListingInput.CategoryId), int.MaxValue, ListingResultStatus.CategoryNotFound]
    ];

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task CreateAndUpdate_RejectInvalidInputWithoutWrites(
        string? memberName, object? invalidValue, ListingResultStatus expectedStatus)
    {
        await using var test = await TestContext.CreateAsync();
        CreateListingInput? input = null;
        if (memberName is not null)
        {
            input = NewInput(test.Original.CategoryId);
            typeof(CreateListingInput).GetProperty(memberName)!.SetValue(input, invalidValue);
        }

        var created = await test.Service.CreateAsync(input, test.ActorId);
        var updated = await test.Service.UpdateAsync(test.Original.Id, ForUpdate(input), test.ActorId);

        foreach (var result in new[] { created, updated })
        {
            Assert.False(result.Succeeded);
            Assert.Equal(expectedStatus, result.Status);
            Assert.NotEmpty(result.Errors);
            if (memberName is not null)
            {
                Assert.Contains(result.Errors, error => error.MemberNames.Contains(memberName));
            }
        }

        await AssertNoWritesAsync(test);
    }

    public static IEnumerable<object?[]> InvalidActors =>
    [
        [null], [string.Empty], [" \t "], [new string('a', 451)]
    ];

    [Theory]
    [MemberData(nameof(InvalidActors))]
    public async Task CreateAndUpdate_RejectInvalidActorWithoutWrites(string? actorId)
    {
        await using var test = await TestContext.CreateAsync();
        var input = NewInput(test.Original.CategoryId);

        var created = await test.Service.CreateAsync(input, actorId);
        var updated = await test.Service.UpdateAsync(test.Original.Id, ForUpdate(input), actorId);

        Assert.Equal(ListingResultStatus.InvalidActor, created.Status);
        Assert.Equal(ListingResultStatus.InvalidActor, updated.Status);
        await AssertNoWritesAsync(test);
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Available)]
    [InlineData(ListingStatus.Missing)]
    public async Task Update_EditableListingPreservesStatusAndCreationTimeAndWritesAudit(ListingStatus status)
    {
        await using var test = await TestContext.CreateAsync();
        await test.Database.Listings.Where(row => row.Id == test.Original.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, status));
        var otherCategory = await test.Database.Categories.Where(category => category.Id != test.Original.CategoryId)
            .Select(category => category.Id).FirstAsync();
        var input = ForUpdate(NewInput(otherCategory));
        var startedAt = DateTime.UtcNow;

        var result = await test.Service.UpdateAsync(test.Original.Id, input, test.ActorId);

        Assert.True(result.Succeeded);
        Assert.Equal(test.Original.Id, result.Value);
        var listing = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == test.Original.Id);
        Assert.Equal("Service listing", listing.Title);
        Assert.Equal("Service description", listing.Description);
        Assert.Equal("Good", listing.Condition);
        Assert.Equal("M", listing.Size);
        Assert.Equal(otherCategory, listing.CategoryId);
        Assert.Equal(status, listing.Status);
        Assert.Equal(test.Original.CreatedAtUtc, listing.CreatedAtUtc);
        var audit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(listing.Id, audit.ListingId);
        Assert.Equal(test.ActorId, audit.ActorUserId);
        Assert.Equal(InventoryAuditActions.Updated, audit.Action);
        Assert.InRange(audit.OccurredAtUtc, startedAt, DateTime.UtcNow);
    }

    [Theory]
    [InlineData(ListingStatus.Held)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Archived)]
    public async Task EditAndUpdate_RejectProtectedStatusesWithoutAudit(ListingStatus status)
    {
        await using var test = await TestContext.CreateAsync();
        await test.Database.Listings.Where(row => row.Id == test.Original.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, status));

        var edit = await test.Service.GetForEditAsync(test.Original.Id);
        var updated = await test.Service.UpdateAsync(test.Original.Id, ForUpdate(NewInput(test.Original.CategoryId)), test.ActorId);

        Assert.Equal(ListingResultStatus.NotEditable, edit.Status);
        Assert.Null(edit.Value);
        Assert.Equal(ListingResultStatus.NotEditable, updated.Status);
        var listing = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == test.Original.Id);
        Assert.Equal(test.Original.Title, listing.Title);
        Assert.Equal(status, listing.Status);
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task EditAndUpdate_MissingListingReturnNotFoundWithoutWrites()
    {
        await using var test = await TestContext.CreateAsync();

        var edit = await test.Service.GetForEditAsync(int.MaxValue);
        var updated = await test.Service.UpdateAsync(int.MaxValue, ForUpdate(NewInput(test.Original.CategoryId)), test.ActorId);

        Assert.Equal(ListingResultStatus.NotFound, edit.Status);
        Assert.Null(edit.Value);
        Assert.Equal(ListingResultStatus.NotFound, updated.Status);
        await AssertNoWritesAsync(test);
    }

    [Fact]
    public async Task GetForEdit_ReturnsEditableFieldsWithoutTrackingListing()
    {
        await using var test = await TestContext.CreateAsync();

        var result = await test.Service.GetForEditAsync(test.Original.Id);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(test.Original.Title, result.Value.Title);
        Assert.Equal(test.Original.Description, result.Value.Description);
        Assert.Equal(test.Original.Condition, result.Value.Condition);
        Assert.Equal(test.Original.Size, result.Value.Size);
        Assert.Equal(test.Original.CategoryId, result.Value.CategoryId);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetInventory_IncludesCategoriesAndProtectedListingsWithoutTracking()
    {
        await using var test = await TestContext.CreateAsync();
        await test.Database.Listings.Where(row => row.Id == test.Original.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Held));

        var inventory = await test.Service.GetInventoryAsync();

        Assert.Equal(test.InitialCount, inventory.Count);
        Assert.Contains(inventory, listing => listing.Id == test.Original.Id && listing.Status == ListingStatus.Held);
        Assert.All(inventory, listing =>
        {
            Assert.NotNull(listing.Category);
            Assert.Equal(listing.CategoryId, listing.Category.Id);
            Assert.False(string.IsNullOrWhiteSpace(listing.Category.Name));
        });
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndUpdate_WhenAuditInsertFailsRollBackListingWrite(bool update)
    {
        await using var test = await TestContext.CreateAsync();
        await test.Database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectAuditInsert BEFORE INSERT ON "AuditEvents"
            BEGIN SELECT RAISE(ABORT, 'Audit insert rejected for test'); END;
            """);
        var input = NewInput(test.Original.CategoryId);

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            if (update)
            {
                await test.Service.UpdateAsync(test.Original.Id, ForUpdate(input), test.ActorId);
            }
            else
            {
                await test.Service.CreateAsync(input, test.ActorId);
            }
        });

        test.Database.ChangeTracker.Clear();
        await AssertNoWritesAsync(test);
    }

    [Fact]
    public async Task EditAndUpdate_CheckDatabaseStatusDespiteStaleTrackedListing()
    {
        await using var test = await TestContext.CreateAsync();
        var staleListing = await test.Database.Listings.SingleAsync(row => row.Id == test.Original.Id);
        Assert.Equal(ListingStatus.Available, staleListing.Status);
        await using (var otherContext = new ApplicationDbContext(test.Options))
        {
            await otherContext.Listings.Where(row => row.Id == staleListing.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Held));
        }
        Assert.Equal(ListingStatus.Available, staleListing.Status);

        var edit = await test.Service.GetForEditAsync(staleListing.Id);
        var updated = await test.Service.UpdateAsync(staleListing.Id, ForUpdate(NewInput(staleListing.CategoryId)), test.ActorId);

        Assert.Equal(ListingResultStatus.NotEditable, edit.Status);
        Assert.Equal(ListingResultStatus.NotEditable, updated.Status);
        var persisted = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == staleListing.Id);
        Assert.Equal(ListingStatus.Held, persisted.Status);
        Assert.Equal(test.Original.Title, persisted.Title);
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
    }

    private static CreateListingInput NewInput(int categoryId) => new()
    {
        Title = "  Service listing  ",
        Description = "  Service description  ",
        Condition = "  Good  ",
        Size = " M ",
        CategoryId = categoryId
    };

    private static UpdateListingInput? ForUpdate(CreateListingInput? input) => input is null ? null : new()
    {
        Title = input.Title,
        Description = input.Description,
        Condition = input.Condition,
        Size = input.Size,
        CategoryId = input.CategoryId
    };

    private static async Task AssertNoWritesAsync(TestContext test)
    {
        Assert.Equal(test.InitialCount, await test.Database.Listings.CountAsync());
        Assert.Empty(await test.Database.AuditEvents.ToListAsync());
        var persisted = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == test.Original.Id);
        Assert.Equal(test.Original.Title, persisted.Title);
        Assert.Equal(test.Original.Description, persisted.Description);
        Assert.Equal(test.Original.Condition, persisted.Condition);
        Assert.Equal(test.Original.Size, persisted.Size);
        Assert.Equal(test.Original.CategoryId, persisted.CategoryId);
        Assert.Equal(test.Original.Status, persisted.Status);
        Assert.Equal(test.Original.CreatedAtUtc, persisted.CreatedAtUtc);
    }

    private sealed class TestContext(
        SqliteConnection connection, DbContextOptions<ApplicationDbContext> options,
        ApplicationDbContext database, Listing original, int initialCount) : IAsyncDisposable
    {
        public DbContextOptions<ApplicationDbContext> Options { get; } = options;
        public ApplicationDbContext Database { get; } = database;
        public ListingService Service { get; } = new(database);
        public Listing Original { get; } = original;
        public int InitialCount { get; } = initialCount;
        public string ActorId { get; } = Guid.NewGuid().ToString();

        public static async Task<TestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var database = new ApplicationDbContext(options);
            await database.Database.EnsureCreatedAsync();
            var original = await database.Listings.AsNoTracking().OrderBy(listing => listing.Id).FirstAsync();
            return new TestContext(connection, options, database, original, await database.Listings.CountAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
