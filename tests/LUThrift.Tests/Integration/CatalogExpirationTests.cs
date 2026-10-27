using System.Text.Json;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests.Integration;

public class CatalogExpirationTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 1, 5, 16, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, ListingStatus.Held)]
    [InlineData(0, ListingStatus.Available)]
    [InlineData(1, ListingStatus.Available)]
    public async Task DeadlineBoundary_ChangesCatalogAvailabilityWithoutChangingStoredHistory(long offsetTicks, ListingStatus expected)
    {
        await using var test = await TestContext.CreateAsync(Deadline.AddTicks(offsetTicks));
        var listing = test.Listing();
        await test.SeedAsync([listing], test.Reservation(listing));
        var before = await test.SnapshotAsync();

        var available = await test.Service.GetPageAsync(new());
        var reserved = await test.Service.GetPageAsync(new() { Availability = CatalogAvailability.Reserved });
        var details = await test.Service.GetDetailsAsync(listing.Id);

        Assert.Equal(expected == ListingStatus.Available ? 1 : 0, available.TotalCount);
        Assert.Equal(expected == ListingStatus.Held ? 1 : 0, reserved.TotalCount);
        Assert.Equal(expected, Assert.Single(expected == ListingStatus.Available ? available.Items : reserved.Items).Status);
        Assert.NotNull(details);
        Assert.Equal(expected, details.Status);
        var json = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(test.UserId, json);
        Assert.DoesNotContain("UserId", json);
        Assert.DoesNotContain("ExpiresAtUtc", json);
        await test.AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task ExpiredAvailability_IsFilteredAndCountedBeforePaginationAlongsideOtherFilters()
    {
        await using var test = await TestContext.CreateAsync(Deadline);
        var expired = Enumerable.Range(0, 13).Select(_ => test.Listing()).ToArray();
        var live = test.Listing();
        var orphan = test.Listing();
        var otherCategory = test.Listing();
        otherCategory.CategoryId = 2;
        var otherSize = test.Listing();
        otherSize.Size = "L";
        var hidden = test.Listing(ListingStatus.Archived);
        await test.SeedAsync([.. expired, live, orphan, otherCategory, otherSize, hidden],
            [.. expired.Select(listing => test.Reservation(listing)),
                test.Reservation(live, expiresAt: Deadline.AddMinutes(30)),
                test.Reservation(otherCategory), test.Reservation(otherSize), test.Reservation(hidden)]);
        var before = await test.SnapshotAsync();
        var filter = new CatalogFilterInput { Search = " expiry ", CategoryId = 1, Size = " M " };
        var first = await test.Service.GetPageAsync(filter);
        Assert.Equal(1, test.Clock.ReadCount); // Count and items share one captured deadline.
        var second = await test.Service.GetPageAsync(filter, 2);

        Assert.Equal(13, first.TotalCount);
        Assert.Equal(13, second.TotalCount);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(12, first.Items.Count);
        Assert.Single(second.Items);
        Assert.Equal(expired.OrderByDescending(listing => listing.Id).Select(listing => listing.Id),
            first.Items.Concat(second.Items).Select(listing => listing.Id));
        Assert.All(first.Items.Concat(second.Items), listing => Assert.Equal(ListingStatus.Available, listing.Status));
        filter.Availability = CatalogAvailability.Reserved;
        var reserved = await test.Service.GetPageAsync(filter);
        Assert.Equal(2, reserved.TotalCount);
        Assert.Equal(new[] { live.Id, orphan.Id }.Order(), reserved.Items.Select(listing => listing.Id).Order());
        filter.Availability = CatalogAvailability.All;
        Assert.Equal(15, (await test.Service.GetPageAsync(filter)).TotalCount);
        await test.AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task OrphanHold_RemainsReservedWithoutGuessingItsOwner()
    {
        await using var test = await TestContext.CreateAsync(Deadline);
        var listing = test.Listing();
        await test.SeedAsync([listing]);
        var before = await test.SnapshotAsync();

        Assert.Empty(await test.Service.GetListingsAsync());
        var reserved = await test.Service.GetListingsAsync(new CatalogFilterInput { Availability = CatalogAvailability.Reserved });
        Assert.Equal(ListingStatus.Held, Assert.Single(reserved).Status);
        Assert.Equal(ListingStatus.Held, (await test.Service.GetDetailsAsync(listing.Id))!.Status);
        await test.AssertUnchangedAsync(before);
    }

    [Theory]
    [InlineData(ReservationStatus.Canceled, false)]
    [InlineData(ReservationStatus.Expired, false)]
    [InlineData(ReservationStatus.Fulfilled, false)]
    [InlineData(ReservationStatus.Canceled, true)]
    [InlineData(ReservationStatus.Expired, true)]
    [InlineData(ReservationStatus.Fulfilled, true)]
    public async Task NewerReservationHistory_PreventsAnOldExpiredRowFromReleasingTheDisplayedHold(
        ReservationStatus newerStatus, bool equalCreatedAt)
    {
        await using var test = await TestContext.CreateAsync(Deadline);
        var listing = test.Listing();
        var expired = test.Reservation(listing);
        expired.Id = 900;
        var newer = test.Reservation(listing, newerStatus);
        newer.Id = 901;
        newer.CreatedAtUtc = equalCreatedAt ? expired.CreatedAtUtc : expired.CreatedAtUtc.AddMinutes(1);
        await test.SeedAsync([listing], expired, newer);
        var before = await test.SnapshotAsync();

        Assert.Empty(await test.Service.GetListingsAsync());
        Assert.Equal(ListingStatus.Held, (await test.Service.GetDetailsAsync(listing.Id))!.Status);
        await test.AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task LatestReservation_IsChosenByCreationTimeBeforeTheIdTieBreaker()
    {
        await using var test = await TestContext.CreateAsync(Deadline);
        var listing = test.Listing();
        var expired = test.Reservation(listing);
        expired.Id = 900;
        var older = test.Reservation(listing, ReservationStatus.Canceled);
        older.Id = 1000;
        older.CreatedAtUtc = expired.CreatedAtUtc.AddHours(-1);
        await test.SeedAsync([listing], expired, older);
        var before = await test.SnapshotAsync();

        Assert.Equal(ListingStatus.Available, Assert.Single(await test.Service.GetListingsAsync()).Status);
        Assert.Equal(ListingStatus.Available, (await test.Service.GetDetailsAsync(listing.Id))!.Status);
        await test.AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task NewerLiveReservation_StaysReservedDespiteExpiredHistory()
    {
        await using var test = await TestContext.CreateAsync(Deadline);
        var listing = test.Listing();
        var expiredHistory = test.Reservation(listing, ReservationStatus.Expired);
        var live = test.Reservation(listing, expiresAt: Deadline.AddMinutes(30));
        live.CreatedAtUtc = expiredHistory.CreatedAtUtc.AddMinutes(30);
        await test.SeedAsync([listing], expiredHistory, live);
        var before = await test.SnapshotAsync();

        Assert.Empty(await test.Service.GetListingsAsync());
        Assert.Equal(ListingStatus.Held, (await test.Service.GetDetailsAsync(listing.Id))!.Status);
        await test.AssertUnchangedAsync(before);
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Missing)]
    [InlineData(ListingStatus.Archived)]
    public async Task ExpiredReservation_NeverMakesAPrivateListingPublic(ListingStatus status)
    {
        await using var test = await TestContext.CreateAsync(Deadline);
        var listing = test.Listing(status);
        await test.SeedAsync([listing], test.Reservation(listing));
        var before = await test.SnapshotAsync();

        foreach (var availability in Enum.GetValues<CatalogAvailability>())
        {
            var page = await test.Service.GetPageAsync(new() { Availability = availability });
            Assert.Equal(0, page.TotalCount);
            Assert.Empty(page.Items);
        }
        Assert.Null(await test.Service.GetDetailsAsync(listing.Id));
        await test.AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task ConflictingLegacyActiveRows_KeepTheDisplayedHoldEvenWhenBothHaveExpired()
    {
        await using var test = await TestContext.CreateAsync(Deadline);
        // Model-only corruption fixture: normal writes are protected by this index.
        await test.Database.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_Reservations_ActiveListing\";");
        var listing = test.Listing();
        var older = test.Reservation(listing);
        var newer = test.Reservation(listing);
        newer.CreatedAtUtc = older.CreatedAtUtc.AddMinutes(1);
        await test.SeedAsync([listing], older, newer);
        var before = await test.SnapshotAsync();

        Assert.Empty(await test.Service.GetListingsAsync());
        Assert.Equal(ListingStatus.Held, (await test.Service.GetDetailsAsync(listing.Id))!.Status);
        await test.AssertUnchangedAsync(before);
    }

    private sealed class ControlledClock(DateTimeOffset now) : TimeProvider
    {
        public int ReadCount { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return now;
        }
    }

    private sealed record Snapshot(object[] Listings, object[] Reservations, object[] Audits);

    private sealed class TestContext(SqliteConnection connection, ApplicationDbContext database, ControlledClock clock)
        : IAsyncDisposable
    {
        private int nextListingId = 100;
        public string UserId { get; } = "catalog-expiration-test-user";
        public ApplicationDbContext Database { get; } = database;
        public ControlledClock Clock { get; } = clock;
        public CatalogService Service { get; } = new(database, clock);

        public static async Task<TestContext> CreateAsync(DateTimeOffset now)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var database = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).Options);
            await database.Database.EnsureCreatedAsync();
            await database.Listings.ExecuteDeleteAsync();
            var test = new TestContext(connection, database, new ControlledClock(now));
            database.Users.Add(new ApplicationUser { Id = test.UserId, DisplayName = "Catalog expiration test" });
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            return test;
        }

        public Listing Listing(ListingStatus status = ListingStatus.Held) => new()
        {
            Id = ++nextListingId, Title = "Expiry fixture item", Description = "Public description.",
            Condition = "Good", Size = "M", CategoryId = 1, Status = status,
            CreatedAtUtc = Deadline.AddDays(-1).UtcDateTime
        };

        public Reservation Reservation(Listing listing, ReservationStatus status = ReservationStatus.Active,
            DateTimeOffset? expiresAt = null) => new()
        {
            ListingId = listing.Id, UserId = UserId, Status = status,
            CreatedAtUtc = Deadline.AddHours(-1).UtcDateTime,
            ExpiresAtUtc = (expiresAt ?? Deadline).UtcDateTime
        };

        public async Task SeedAsync(Listing[] listings, params Reservation[] reservations)
        {
            Database.Listings.AddRange(listings);
            Database.Reservations.AddRange(reservations);
            Database.AuditEvents.Add(new AuditEvent
            {
                ListingId = listings[0].Id, ActorUserId = UserId, Action = InventoryAuditActions.Reserved,
                OccurredAtUtc = Deadline.AddHours(-1).UtcDateTime, Details = "Existing audit history."
            });
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
        }

        public async Task<Snapshot> SnapshotAsync()
        {
            var listings = await Database.Listings.AsNoTracking().OrderBy(listing => listing.Id).ToArrayAsync();
            var reservations = await Database.Reservations.AsNoTracking().OrderBy(reservation => reservation.Id).ToArrayAsync();
            var audits = await Database.AuditEvents.AsNoTracking().OrderBy(audit => audit.Id).ToArrayAsync();
            return new Snapshot(
                listings.Select(listing => (object)(listing.Id, listing.Title, listing.Description, listing.CategoryId,
                    listing.Condition, listing.Size, listing.Status, listing.CreatedAtUtc)).ToArray(),
                reservations.Select(reservation => (object)(reservation.Id, reservation.ListingId, reservation.UserId,
                    reservation.Status, reservation.CreatedAtUtc, reservation.ExpiresAtUtc,
                    reservation.FulfilledAtUtc, reservation.CancellationReason)).ToArray(),
                audits.Select(audit => (object)(audit.Id, audit.ListingId, audit.ActorUserId, audit.Action,
                    audit.OccurredAtUtc, audit.Details)).ToArray());
        }

        public async Task AssertUnchangedAsync(Snapshot before)
        {
            var after = await SnapshotAsync();
            Assert.Equal(before.Listings, after.Listings);
            Assert.Equal(before.Reservations, after.Reservations);
            Assert.Equal(before.Audits, after.Audits);
            Assert.Empty(Database.ChangeTracker.Entries());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
