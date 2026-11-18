using System.Text.Json;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests.Integration;

public class ListingAuthorizationTests
{
    private static readonly string[] Operations = ["Create", "Update", "Publish", "Archive", "Missing", "Collect"];

    public static IEnumerable<object[]> RejectedActors =>
        from operation in Operations
        from actorState in new[] { "revoked", "inactive", "deleted", "student" }
        select new object[] { operation, actorState };

    [Theory]
    [MemberData(nameof(RejectedActors))]
    public async Task Mutation_RequiresCurrentActiveStaffEvenWithStaleTrackedAccount(string operation, string actorState)
    {
        await using var test = await TestContext.CreateAsync(operation,
            actorState == "student" ? ApplicationRoles.Student : ApplicationRoles.Staff);
        // Deliberately leave this active user tracked while changing persisted authorization.
        var tracked = await test.Database.Users.SingleAsync(row => row.Id == test.ActorId);
        switch (actorState)
        {
            case "revoked":
                await test.Database.UserRoles.Where(row => row.UserId == test.ActorId).ExecuteDeleteAsync();
                break;
            case "inactive":
                await test.Database.Users.Where(row => row.Id == test.ActorId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
                Assert.True(tracked.IsActive);
                break;
            case "deleted":
                await test.Database.Users.Where(row => row.Id == test.ActorId).ExecuteDeleteAsync();
                break;
        }
        var before = await test.InventorySnapshotAsync();

        var result = await test.ExecuteAsync(operation);

        Assert.Equal(ListingResultStatus.InvalidActor, result.Status);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(before, await test.InventorySnapshotAsync());
        Assert.Empty(await test.Database.AuditEvents.AsNoTracking().ToListAsync());
    }

    public static IEnumerable<object[]> AuthorizedActors =>
        from operation in Operations
        from role in new[] { ApplicationRoles.Staff, ApplicationRoles.Administrator }
        select new object[] { operation, role };

    [Theory]
    [MemberData(nameof(AuthorizedActors))]
    public async Task Mutation_CurrentStaffOrAdministratorSucceedsWithAuthenticatedActorAudit(string operation, string role)
    {
        await using var test = await TestContext.CreateAsync(operation, role);

        var result = await test.ExecuteAsync(operation);

        Assert.True(result.Succeeded);
        var audit = await test.Database.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(test.ActorId, audit.ActorUserId);
        Assert.Equal(result.Value, audit.ListingId);
    }

    public static IEnumerable<object[]> AllOperations => Operations.Select(operation => new object[] { operation });

    [Theory]
    [MemberData(nameof(AllOperations))]
    public async Task Mutation_DeactivationDuringPersistenceRollsBackInventoryAndAudit(string operation)
    {
        await using var test = await TestContext.CreateAsync(operation, ApplicationRoles.Staff);
        var before = await test.InventorySnapshotAsync();
        // A deterministic database-side permission change after SaveChanges has written
        // the audit exercises the final permission check and transaction rollback.
        await test.Database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER DeactivateInventoryActor AFTER INSERT ON "AuditEvents"
            BEGIN UPDATE "AspNetUsers" SET "IsActive" = 0 WHERE "Id" = NEW."ActorUserId"; END;
            """);

        var result = await test.ExecuteAsync(operation);

        Assert.Equal(ListingResultStatus.InvalidActor, result.Status);
        Assert.Equal(before, await test.InventorySnapshotAsync());
        Assert.Empty(await test.Database.AuditEvents.AsNoTracking().ToListAsync());
        Assert.True(await test.Database.Users.Where(row => row.Id == test.ActorId).Select(row => row.IsActive).SingleAsync());
        // A later save on this context must not accidentally retry a rejected write.
        await test.Database.SaveChangesAsync();
        Assert.Equal(before, await test.InventorySnapshotAsync());
        Assert.Empty(await test.Database.AuditEvents.AsNoTracking().ToListAsync());
    }

    private sealed class TestContext(SqliteConnection connection, ApplicationDbContext database, Listing listing,
        string actorId) : IAsyncDisposable
    {
        public ApplicationDbContext Database { get; } = database;
        public string ActorId { get; } = actorId;

        public static async Task<TestContext> CreateAsync(string operation, string roleName)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var database = new ApplicationDbContext(options);
            await database.Database.EnsureCreatedAsync();
            var listing = await database.Listings.OrderBy(row => row.Id).FirstAsync();
            listing.Status = operation is "Missing" or "Collect" ? ListingStatus.Available : ListingStatus.Draft;
            var actor = new ApplicationUser { DisplayName = "Inventory test operator", IsActive = true };
            var role = new IdentityRole(roleName);
            database.Users.Add(actor);
            database.Roles.Add(role);
            database.UserRoles.Add(new IdentityUserRole<string> { UserId = actor.Id, RoleId = role.Id });
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            return new(connection, database, listing, actor.Id);
        }

        public Task<ListingResult<int>> ExecuteAsync(string operation)
        {
            var service = new ListingService(Database);
            var input = new CreateListingInput
            {
                Title = "Updated inventory item", Description = "Synthetic item description",
                Condition = "Good", Size = "M", CategoryId = listing.CategoryId
            };
            return operation switch
            {
                "Create" => service.CreateAsync(input, ActorId),
                "Update" => service.UpdateAsync(listing.Id, new UpdateListingInput
                {
                    Title = input.Title, Description = input.Description, Condition = input.Condition,
                    Size = input.Size, CategoryId = input.CategoryId
                }, ActorId),
                "Publish" => service.PublishAsync(listing.Id, ActorId),
                "Archive" => service.ArchiveAsync(listing.Id, ActorId),
                "Missing" => service.MarkMissingAsync(listing.Id, ActorId),
                "Collect" => service.RecordWalkInCollectionAsync(listing.Id, ActorId),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }

        public async Task<string> InventorySnapshotAsync() => JsonSerializer.Serialize(
            await Database.Listings.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id, row.Title, row.Description, row.Condition, row.Size,
                row.CategoryId, row.Status, row.CreatedAtUtc
            }).ToArrayAsync());

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
