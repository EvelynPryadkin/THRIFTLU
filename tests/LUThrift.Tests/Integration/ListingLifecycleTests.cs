using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests.Integration;

public class ListingLifecycleTests
{
    private static readonly (string Action, ListingStatus Source, ListingStatus Target, string AuditAction)[] Transitions =
    [
        (nameof(ListingService.PublishAsync), ListingStatus.Draft, ListingStatus.Available, InventoryAuditActions.Published),
        (nameof(ListingService.MarkMissingAsync), ListingStatus.Available, ListingStatus.Missing, InventoryAuditActions.MarkedMissing),
        (nameof(ListingService.RecordWalkInCollectionAsync), ListingStatus.Available, ListingStatus.Collected, InventoryAuditActions.WalkInCollected),
        (nameof(ListingService.ArchiveAsync), ListingStatus.Draft, ListingStatus.Archived, InventoryAuditActions.Archived),
        (nameof(ListingService.ArchiveAsync), ListingStatus.Available, ListingStatus.Archived, InventoryAuditActions.Archived),
        (nameof(ListingService.ArchiveAsync), ListingStatus.Missing, ListingStatus.Archived, InventoryAuditActions.Archived)
    ];

    public static IEnumerable<object[]> AllowedTransitions => Transitions.Select(transition => new object[]
    {
        transition.Action, transition.Source, transition.Target, transition.AuditAction
    });

    public static IEnumerable<object[]> Actions => Transitions.DistinctBy(transition => transition.Action)
        .Select(transition => new object[] { transition.Action, transition.Source });

    public static IEnumerable<object[]> DisallowedTransitions =>
        from action in Transitions.Select(transition => transition.Action).Distinct()
        from status in Enum.GetValues<ListingStatus>()
        where !Transitions.Any(transition => transition.Action == action && transition.Source == status)
        select new object[] { action, status };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public async Task AllowedTransition_ChangesOnlyStatusAndAddsOneAudit_RepeatIsRejected(
        string action, ListingStatus source, ListingStatus target, string auditAction)
    {
        await using var test = await TestContext.CreateAsync(source);
        var startedAt = DateTime.UtcNow;

        var result = await ExecuteAsync(test.Service, action, test.Original.Id, test.ActorId);

        Assert.True(result.Succeeded);
        Assert.Equal(ListingResultStatus.Success, result.Status);
        Assert.Equal(test.Original.Id, result.Value);
        Assert.Empty(result.Errors);
        var persisted = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == test.Original.Id);
        AssertListingMatches(test.Original, persisted, target);
        Assert.Equal(test.InitialCount, await test.Database.Listings.CountAsync());
        var audit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(test.Original.Id, audit.ListingId);
        Assert.Equal(test.ActorId, audit.ActorUserId);
        Assert.Equal(auditAction, audit.Action);
        Assert.InRange(audit.OccurredAtUtc, startedAt, DateTime.UtcNow);
        Assert.NotNull(audit.Details);
        Assert.Contains(source.ToString(), audit.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(target.ToString(), audit.Details, StringComparison.OrdinalIgnoreCase);

        var repeated = await ExecuteAsync(test.Service, action, test.Original.Id, test.ActorId);

        Assert.False(repeated.Succeeded);
        Assert.Equal(ListingResultStatus.InvalidTransition, repeated.Status);
        var afterRepeat = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == test.Original.Id);
        AssertListingMatches(test.Original, afterRepeat, target);
        var retainedAudit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(audit.Id, retainedAudit.Id);
        Assert.Equal(audit.Details, retainedAudit.Details);
        Assert.Equal(audit.OccurredAtUtc, retainedAudit.OccurredAtUtc);
    }

    [Theory]
    [MemberData(nameof(DisallowedTransitions))]
    public async Task DisallowedTransition_PreservesListingWithoutAudit(string action, ListingStatus source)
    {
        await using var test = await TestContext.CreateAsync(source);

        var result = await ExecuteAsync(test.Service, action, test.Original.Id, test.ActorId);

        Assert.False(result.Succeeded);
        Assert.Equal(ListingResultStatus.InvalidTransition, result.Status);
        Assert.NotEmpty(result.Errors);
        await AssertNoWritesAsync(test);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Transition_RejectsUnknownListingAndInvalidActorsWithoutWrites(string action, ListingStatus source)
    {
        await using var test = await TestContext.CreateAsync(source);

        var missing = await ExecuteAsync(test.Service, action, int.MaxValue, test.ActorId);

        Assert.False(missing.Succeeded);
        Assert.Equal(ListingResultStatus.NotFound, missing.Status);
        foreach (var actorId in new string?[] { null, string.Empty, " \t ", new('a', 451) })
        {
            var invalidActor = await ExecuteAsync(test.Service, action, test.Original.Id, actorId);

            Assert.False(invalidActor.Succeeded);
            Assert.Equal(ListingResultStatus.InvalidActor, invalidActor.Status);
        }

        await AssertNoWritesAsync(test);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Transition_WhenAuditInsertFailsRollsBackStatus(string action, ListingStatus source)
    {
        await using var test = await TestContext.CreateAsync(source);
        await test.Database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectAuditInsert BEFORE INSERT ON "AuditEvents"
            BEGIN SELECT RAISE(ABORT, 'Audit insert rejected for test'); END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            ExecuteAsync(test.Service, action, test.Original.Id, test.ActorId));

        await AssertNoWritesAsync(test);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Transition_UsesCurrentDatabaseStatusDespiteStaleTrackedListing(string action, ListingStatus source)
    {
        await using var test = await TestContext.CreateAsync(source);
        var tracked = await test.Database.Listings.SingleAsync(row => row.Id == test.Original.Id);
        await using (var otherContext = new ApplicationDbContext(test.Options))
        {
            await otherContext.Listings.Where(row => row.Id == tracked.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ListingStatus.Held));
        }
        Assert.Equal(source, tracked.Status);

        var result = await ExecuteAsync(test.Service, action, tracked.Id, test.ActorId);

        Assert.False(result.Succeeded);
        Assert.Equal(ListingResultStatus.InvalidTransition, result.Status);
        var persisted = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == tracked.Id);
        AssertListingMatches(test.Original, persisted, ListingStatus.Held);
        Assert.Empty(await test.Database.AuditEvents.AsNoTracking().ToListAsync());
    }

    private static Task<ListingResult<int>> ExecuteAsync(ListingService service, string action, int listingId, string? actorId)
        => action switch
        {
            nameof(ListingService.PublishAsync) => service.PublishAsync(listingId, actorId),
            nameof(ListingService.ArchiveAsync) => service.ArchiveAsync(listingId, actorId),
            nameof(ListingService.MarkMissingAsync) => service.MarkMissingAsync(listingId, actorId),
            nameof(ListingService.RecordWalkInCollectionAsync) => service.RecordWalkInCollectionAsync(listingId, actorId),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown lifecycle action.")
        };

    private static async Task AssertNoWritesAsync(TestContext test)
    {
        Assert.Equal(test.InitialCount, await test.Database.Listings.CountAsync());
        Assert.Empty(await test.Database.AuditEvents.AsNoTracking().ToListAsync());
        var persisted = await test.Database.Listings.AsNoTracking().SingleAsync(row => row.Id == test.Original.Id);
        AssertListingMatches(test.Original, persisted, test.Original.Status);
    }

    private static void AssertListingMatches(Listing original, Listing persisted, ListingStatus expectedStatus)
    {
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal(original.Title, persisted.Title);
        Assert.Equal(original.Description, persisted.Description);
        Assert.Equal(original.Condition, persisted.Condition);
        Assert.Equal(original.Size, persisted.Size);
        Assert.Equal(original.CategoryId, persisted.CategoryId);
        Assert.Equal(original.CreatedAtUtc, persisted.CreatedAtUtc);
        Assert.Equal(expectedStatus, persisted.Status);
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

        public static async Task<TestContext> CreateAsync(ListingStatus status)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var database = new ApplicationDbContext(options);
            await database.Database.EnsureCreatedAsync();
            var listingId = await database.Listings.OrderBy(listing => listing.Id).Select(listing => listing.Id).FirstAsync();
            await database.Listings.Where(listing => listing.Id == listingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, status));
            var original = await database.Listings.AsNoTracking().SingleAsync(listing => listing.Id == listingId);
            return new TestContext(connection, options, database, original, await database.Listings.CountAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
