using System.Text.Json;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace LUThrift.Tests.Integration;

public partial class ReservationConcurrencyTests
{
    private const string ImageMigration = "20261105162336_AddListingImages";

    [ReservationPostgresFact]
    public async Task ImageMigration_UpgradesExistingInventoryAndHistoryWithoutChangingThem()
    {
        // Start before the image migration and reuse the guarded, disposable PostgreSQL schema.
        await using var test = await TestDatabase.CreateAsync(previousMigration: true);
        await using var database = test.Context("image-migration");
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261026011302_EnforceSingleActiveReservationPerListing");
        Assert.DoesNotContain(ImageMigration, await database.Database.GetAppliedMigrationsAsync());

        var studentId = await test.AddStudentAsync();
        var listingId = await test.AddListingAsync(ListingStatus.Held);
        var reservationId = await test.AddReservationAsync(studentId, listingId, InitialNow.AddHours(1));
        database.AuditEvents.Add(new AuditEvent
        {
            ListingId = listingId,
            ActorUserId = studentId,
            Action = "ImageMigrationFixture",
            OccurredAtUtc = InitialNow.UtcDateTime,
            Details = $"Synthetic reservation {reservationId}; Available to Held."
        });
        await database.SaveChangesAsync();
        var before = await ImageMigrationHistoryAsync(database);

        await migrator.MigrateAsync(ImageMigration);

        Assert.Contains(ImageMigration, await database.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync());
        Assert.Empty(await database.ListingImages.AsNoTracking().ToListAsync());
        Assert.Equal(before, await ImageMigrationHistoryAsync(database));

        // Inspect the actual PostgreSQL schema, including UTC timestamps and required lengths.
        await database.Database.OpenConnectionAsync();
        await using (var command = database.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = """
                SELECT column_name, data_type, is_nullable, character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = 'ListingImages'
                """;
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new Dictionary<string, (string Type, bool Required, int? Length)>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2) == "NO",
                    reader.IsDBNull(3) ? null : reader.GetInt32(3)));
            }

            Assert.Equal(8, columns.Count);
            Assert.Equal(("uuid", true, (int?)null), columns["Id"]);
            Assert.Equal(("integer", true, (int?)null), columns["ListingId"]);
            Assert.Equal(("character varying", true, (int?)40), columns["StorageFileName"]);
            Assert.Equal(("character varying", true, (int?)20), columns["ContentType"]);
            Assert.Equal(("bigint", true, (int?)null), columns["ByteLength"]);
            Assert.Equal(("integer", true, (int?)null), columns["Width"]);
            Assert.Equal(("integer", true, (int?)null), columns["Height"]);
            Assert.Equal(("timestamp with time zone", true, (int?)null), columns["CreatedAtUtc"]);
        }

        // A second normal upgrade is harmless and leaves both data and migration history intact.
        var applied = (await database.Database.GetAppliedMigrationsAsync()).ToArray();
        await migrator.MigrateAsync();
        Assert.Equal(applied, (await database.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(before, await ImageMigrationHistoryAsync(database));
    }

    [ReservationPostgresTheory]
    [InlineData(true, "IX_ListingImages_ListingId")]
    [InlineData(false, "IX_ListingImages_StorageFileName")]
    public async Task ImageMigration_RejectsDuplicateListingPhotosAndStorageNames(
        bool sameListing, string expectedConstraint)
    {
        await using var test = await TestDatabase.CreateAsync();
        var firstListing = await test.AddListingAsync();
        var secondListing = await test.AddListingAsync();
        var firstImage = ImageMigrationPhoto(firstListing);
        await using (var database = test.Context("first-photo"))
        {
            database.ListingImages.Add(firstImage);
            await database.SaveChangesAsync();
        }

        await using (var database = test.Context("duplicate-photo"))
        {
            var duplicate = ImageMigrationPhoto(sameListing ? firstListing : secondListing);
            if (!sameListing)
                duplicate.StorageFileName = firstImage.StorageFileName;
            database.ListingImages.Add(duplicate);

            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal(expectedConstraint, postgres.ConstraintName);
        }

        await using var verify = test.Context("verify-photo");
        var saved = Assert.Single(await verify.ListingImages.AsNoTracking().ToListAsync());
        Assert.Equal(firstImage.Id, saved.Id);
        Assert.Equal(firstImage.StorageFileName, saved.StorageFileName);
        Assert.Equal(2, await verify.Listings.CountAsync(item => item.Id == firstListing || item.Id == secondListing));
        Assert.Null(await verify.Listings.Where(item => item.Id == secondListing).Select(item => item.Image).SingleAsync());
    }

    [ReservationPostgresFact]
    public async Task ImageMigration_PhotoIsOptionalButPreventsDeletingItsListingAndCannotBeOrphaned()
    {
        await using var test = await TestDatabase.CreateAsync();
        var withPhoto = await test.AddListingAsync();
        var withoutPhoto = await test.AddListingAsync();
        await using (var database = test.Context("add-photo"))
        {
            database.ListingImages.Add(ImageMigrationPhoto(withPhoto));
            await database.SaveChangesAsync();
        }

        await using (var database = test.Context("delete-listing"))
        {
            // Bypass client-side relationship handling to exercise the real restrictive FK.
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                database.Listings.Where(item => item.Id == withPhoto).ExecuteDeleteAsync());
            Assert.Equal(PostgresErrorCodes.RestrictViolation, failure.SqlState);
            Assert.Equal("FK_ListingImages_Listings_ListingId", failure.ConstraintName);
            Assert.Equal(1, await database.Listings.Where(item => item.Id == withoutPhoto).ExecuteDeleteAsync());
        }

        await using (var database = test.Context("orphan-photo"))
        {
            database.ListingImages.Add(ImageMigrationPhoto(withoutPhoto));
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
            Assert.Equal("FK_ListingImages_Listings_ListingId", postgres.ConstraintName);
        }

        await using var verify = test.Context("verify-photo-fk");
        Assert.True(await verify.Listings.AnyAsync(item => item.Id == withPhoto));
        Assert.Equal(withPhoto, (await verify.ListingImages.AsNoTracking().SingleAsync()).ListingId);
    }

    private static ListingImage ImageMigrationPhoto(int listingId) => new()
    {
        Id = Guid.NewGuid(),
        ListingId = listingId,
        StorageFileName = Guid.NewGuid().ToString("N") + ".png",
        ContentType = "image/png",
        ByteLength = 120,
        Width = 2,
        Height = 3,
        CreatedAtUtc = InitialNow.UtcDateTime
    };

    private static async Task<string> ImageMigrationHistoryAsync(ApplicationDbContext database) =>
        JsonSerializer.Serialize(new
        {
            Listings = await database.Listings.AsNoTracking().OrderBy(item => item.Id).Select(item => new
            {
                item.Id, item.Title, item.Description, item.Condition, item.Size, item.CategoryId,
                item.Status, item.CreatedAtUtc
            }).ToListAsync(),
            Reservations = await database.Reservations.AsNoTracking().OrderBy(item => item.Id).Select(item => new
            {
                item.Id, item.ListingId, item.UserId, item.CreatedAtUtc, item.ExpiresAtUtc,
                item.Status, item.FulfilledAtUtc, item.CancellationReason
            }).ToListAsync(),
            Audits = await database.AuditEvents.AsNoTracking().OrderBy(item => item.Id).Select(item => new
            {
                item.Id, item.ListingId, item.ActorUserId, item.Action, item.OccurredAtUtc, item.Details
            }).ToListAsync()
        });
}
