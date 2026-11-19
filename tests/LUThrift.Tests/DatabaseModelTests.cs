using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests;

public class DatabaseModelTests
{
    [Fact]
    public void PickupScheduleModel_UsesVersionedSingletonAndLocalDateTimeAggregate()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=luthrift_model_test").Options;
        using var context = new ApplicationDbContext(options);
        var schedule = context.Model.FindEntityType(typeof(PickupSchedule))!;
        var id = Assert.Single(schedule.FindPrimaryKey()!.Properties);
        Assert.Equal(nameof(PickupSchedule.Id), id.Name);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, id.ValueGenerated);
        var version = schedule.FindProperty(nameof(PickupSchedule.Version))!;
        Assert.True(version.IsConcurrencyToken);
        Assert.False(version.IsNullable);
        Assert.Equal(typeof(Guid), version.ClrType);
        var zone = schedule.FindProperty(nameof(PickupSchedule.CampusTimeZone))!;
        Assert.False(zone.IsNullable);
        Assert.Equal(100, zone.GetMaxLength());

        var interval = context.Model.FindEntityType(typeof(PickupScheduleInterval))!;
        Assert.Equal([nameof(PickupScheduleInterval.ScheduleId), nameof(PickupScheduleInterval.Day),
            nameof(PickupScheduleInterval.OpensAt)], interval.FindPrimaryKey()!.Properties.Select(property => property.Name));
        var day = interval.FindProperty(nameof(PickupScheduleInterval.Day))!;
        Assert.Equal(9, day.GetMaxLength());
        Assert.Equal("Monday", day.GetTypeMapping().Converter!.ConvertToProvider(DayOfWeek.Monday));
        Assert.Equal("time without time zone", interval.FindProperty(nameof(PickupScheduleInterval.OpensAt))!.GetColumnType());
        Assert.Equal("time without time zone", interval.FindProperty(nameof(PickupScheduleInterval.ClosesAt))!.GetColumnType());

        var closedDate = context.Model.FindEntityType(typeof(PickupScheduleClosedDate))!;
        Assert.Equal([nameof(PickupScheduleClosedDate.ScheduleId), nameof(PickupScheduleClosedDate.Date)],
            closedDate.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal("date", closedDate.FindProperty(nameof(PickupScheduleClosedDate.Date))!.GetColumnType());
        foreach (var child in new[] { interval, closedDate })
        {
            var relationship = Assert.Single(child.GetForeignKeys());
            Assert.Equal(typeof(PickupSchedule), relationship.PrincipalEntityType.ClrType);
            Assert.True(relationship.IsRequired);
            Assert.Equal(DeleteBehavior.Cascade, relationship.DeleteBehavior);
        }
    }

    [Fact]
    public async Task PickupScheduleDatabase_StartsUnconfiguredAndRetainsAuditWhenAggregateIsRemoved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        Assert.Empty(await context.PickupSchedules.ToListAsync());
        Assert.Empty(await context.PickupScheduleIntervals.ToListAsync());
        Assert.Empty(await context.PickupScheduleClosedDates.ToListAsync());
        var schedule = new PickupSchedule
        {
            Version = Guid.NewGuid(), CampusTimeZone = "America/Chicago",
            WeeklyIntervals = [new PickupScheduleInterval
            {
                Day = DayOfWeek.Monday, OpensAt = new TimeOnly(10, 0), ClosesAt = new TimeOnly(12, 0)
            }],
            ClosedDates = [new PickupScheduleClosedDate { Date = new DateOnly(2026, 1, 1) }]
        };
        var audit = new AdministrativeAuditEvent
        {
            ActorUserId = "schedule-test-actor", TargetScheduleId = PickupSchedule.SingletonId,
            Action = AdministrativeAuditActions.PickupScheduleSaved,
            OccurredAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        };
        context.PickupSchedules.Add(schedule);
        context.AdministrativeAuditEvents.Add(audit);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await context.PickupSchedules.Include(row => row.WeeklyIntervals)
            .Include(row => row.ClosedDates).SingleAsync();
        Assert.Equal(schedule.Version, stored.Version);
        Assert.Equal(new TimeOnly(10, 0), Assert.Single(stored.WeeklyIntervals).OpensAt);
        Assert.Equal(new DateOnly(2026, 1, 1), Assert.Single(stored.ClosedDates).Date);
        // Untracked children demonstrate that database cascading belongs only to the aggregate.
        context.ChangeTracker.Clear();
        context.PickupSchedules.Remove(await context.PickupSchedules.SingleAsync());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Empty(await context.PickupScheduleIntervals.ToListAsync());
        Assert.Empty(await context.PickupScheduleClosedDates.ToListAsync());
        var retained = await context.AdministrativeAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(audit.Id, retained.Id);
        Assert.Equal(PickupSchedule.SingletonId, retained.TargetScheduleId);
        Assert.Null(retained.TargetUserId);
    }

    [Theory]
    [InlineData(2, 10, 12)]
    [InlineData(1, 12, 12)]
    [InlineData(1, 13, 12)]
    public async Task PickupScheduleDatabase_RejectsNonSingletonAndNonPositiveIntervals(int id, int opensHour, int closesHour)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        context.PickupSchedules.Add(new PickupSchedule
        {
            Id = id, Version = Guid.NewGuid(), CampusTimeZone = "America/Chicago",
            WeeklyIntervals = [new PickupScheduleInterval
            {
                Day = DayOfWeek.Monday, OpensAt = new TimeOnly(opensHour, 0), ClosesAt = new TimeOnly(closesHour, 0)
            }]
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        Assert.Empty(await context.PickupSchedules.ToListAsync());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("schedule-test-target", 1)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    public async Task AdministrativeAuditDatabase_RequiresExactlyOneValidTarget(string? targetUserId, int? targetScheduleId)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        context.AdministrativeAuditEvents.Add(new AdministrativeAuditEvent
        {
            ActorUserId = "schedule-test-actor", TargetUserId = targetUserId, TargetScheduleId = targetScheduleId,
            Action = AdministrativeAuditActions.PickupScheduleSaved,
            OccurredAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        Assert.Empty(await context.AdministrativeAuditEvents.ToListAsync());
    }

    [Fact]
    public void AdministrativeAuditModel_PreservesIdentifiersWithoutListingOrAccountRelationships()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=luthrift_model_test").Options;
        using var context = new ApplicationDbContext(options);
        var audit = context.Model.FindEntityType(typeof(AdministrativeAuditEvent));
        Assert.NotNull(audit);
        Assert.Equal("AdministrativeAuditEvents", audit.GetTableName());
        var id = Assert.Single(audit.FindPrimaryKey()!.Properties);
        Assert.Equal(nameof(AdministrativeAuditEvent.Id), id.Name);
        Assert.Equal(typeof(int), id.ClrType);
        Assert.Empty(audit.GetForeignKeys());
        Assert.Empty(audit.GetNavigations());
        Assert.Null(audit.FindProperty("ListingId"));

        foreach (var name in new[] { nameof(AdministrativeAuditEvent.ActorUserId), nameof(AdministrativeAuditEvent.TargetUserId) })
        {
            var identifier = audit.FindProperty(name)!;
            Assert.Equal(name == nameof(AdministrativeAuditEvent.TargetUserId), identifier.IsNullable);
            Assert.Equal(450, identifier.GetMaxLength());
        }
        Assert.True(audit.FindProperty(nameof(AdministrativeAuditEvent.TargetScheduleId))!.IsNullable);
        var action = audit.FindProperty(nameof(AdministrativeAuditEvent.Action))!;
        Assert.False(action.IsNullable);
        Assert.Equal(50, action.GetMaxLength());
        var occurredAt = audit.FindProperty(nameof(AdministrativeAuditEvent.OccurredAtUtc))!;
        Assert.False(occurredAt.IsNullable);
        Assert.Equal("timestamp with time zone", occurredAt.GetColumnType());
        Assert.Contains(audit.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(AdministrativeAuditEvent.TargetUserId), nameof(AdministrativeAuditEvent.OccurredAtUtc)]));
        Assert.Contains(audit.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(AdministrativeAuditEvent.TargetScheduleId), nameof(AdministrativeAuditEvent.OccurredAtUtc)]));
    }

    [Fact]
    public async Task AdministrativeAuditHistory_SurvivesActorAndTargetAccountDeletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var actor = new ApplicationUser { DisplayName = "Administrative audit actor" };
        var target = new ApplicationUser { DisplayName = "Administrative audit target" };
        var occurredAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var audit = new AdministrativeAuditEvent
        {
            ActorUserId = actor.Id, TargetUserId = target.Id,
            Action = AdministrativeAuditActions.StaffGranted, OccurredAtUtc = occurredAt
        };
        context.Users.AddRange(actor, target);
        context.AdministrativeAuditEvents.Add(audit);
        await context.SaveChangesAsync();
        context.Users.RemoveRange(actor, target);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.False(await context.Users.AnyAsync(user => user.Id == actor.Id || user.Id == target.Id));
        var retained = await context.AdministrativeAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(audit.Id, retained.Id);
        Assert.Equal(actor.Id, retained.ActorUserId);
        Assert.Equal(target.Id, retained.TargetUserId);
        Assert.Equal(AdministrativeAuditActions.StaffGranted, retained.Action);
        Assert.Equal(occurredAt, retained.OccurredAtUtc);
    }

    [Fact]
    public void ListingImageModel_IsOptionalPerListingAndRequiresOneUniqueRestrictiveRelationship()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=luthrift_model_test").Options;
        using var context = new ApplicationDbContext(options);
        var image = context.Model.FindEntityType(typeof(ListingImage));
        Assert.NotNull(image);
        var relation = Assert.Single(image.GetForeignKeys());
        Assert.Equal(typeof(Listing), relation.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(ListingImage.ListingId), Assert.Single(relation.Properties).Name);
        Assert.True(relation.IsRequired);
        Assert.False(relation.IsRequiredDependent);
        Assert.True(relation.IsUnique);
        Assert.Equal(DeleteBehavior.Restrict, relation.DeleteBehavior);
        Assert.Contains(image.GetIndexes(), index => index.IsUnique
            && index.Properties.Count == 1 && index.Properties[0].Name == nameof(ListingImage.ListingId));
        var key = Assert.Single(image.FindPrimaryKey()!.Properties);
        Assert.Equal(typeof(Guid), key.ClrType);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, key.ValueGenerated);
        Assert.Equal(40, image.FindProperty(nameof(ListingImage.StorageFileName))!.GetMaxLength());
        Assert.False(image.FindProperty(nameof(ListingImage.StorageFileName))!.IsNullable);
        Assert.Equal(20, image.FindProperty(nameof(ListingImage.ContentType))!.GetMaxLength());
        Assert.False(image.FindProperty(nameof(ListingImage.ContentType))!.IsNullable);
        Assert.Equal("timestamp with time zone", image.FindProperty(nameof(ListingImage.CreatedAtUtc))!.GetColumnType());
    }

    [Fact]
    public async Task ListingImageDatabase_PreventsSecondPhotoAndDeletingItsListing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        Assert.Empty(await context.ListingImages.ToListAsync());
        var image = new ListingImage
        {
            Id = Guid.NewGuid(), ListingId = 1, StorageFileName = Guid.NewGuid().ToString("N") + ".png",
            ContentType = "image/png", Width = 1, Height = 1, ByteLength = 100, CreatedAtUtc = DateTime.UtcNow
        };
        context.ListingImages.Add(image);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        context.ListingImages.Add(new ListingImage
        {
            Id = Guid.NewGuid(), ListingId = 1, StorageFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg", Width = 1, Height = 1, ByteLength = 100, CreatedAtUtc = DateTime.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        var listing = await context.Listings.SingleAsync(row => row.Id == 1);
        context.Listings.Remove(listing);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        Assert.Equal(image.Id, (await context.ListingImages.AsNoTracking().SingleAsync()).Id);
        Assert.True(await context.Listings.AnyAsync(row => row.Id == 1));
    }

    [Fact]
    public void ReservationRelationships_AreRequiredAndPreventCascadeDeletion()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=luthrift_model_test")
            .Options;
        using var context = new ApplicationDbContext(options);

        var reservation = context.Model.FindEntityType(typeof(Reservation));
        Assert.NotNull(reservation);

        var foreignKeys = reservation.GetForeignKeys().ToList();
        Assert.Equal(2, foreignKeys.Count);
        Assert.Contains(foreignKeys, key => key.PrincipalEntityType.ClrType == typeof(Listing));
        Assert.Contains(foreignKeys, key => key.PrincipalEntityType.ClrType == typeof(ApplicationUser));
        Assert.All(foreignKeys, key =>
        {
            Assert.True(key.IsRequired);
            Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior);
        });
    }

    [Fact]
    public void ReservationModel_UsesStoredActiveValueForUniqueListingIndex()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=luthrift_model_test").Options;
        using var context = new ApplicationDbContext(options);
        var reservation = context.Model.FindEntityType(typeof(Reservation))!;
        var index = Assert.Single(reservation.GetIndexes(),
            index => index.GetDatabaseName() == "IX_Reservations_ActiveListing");

        Assert.True(index.IsUnique);
        Assert.Equal(nameof(Reservation.ListingId), Assert.Single(index.Properties).Name);
        Assert.Equal("\"Status\" = 'Active'", index.GetFilter());
        Assert.Equal("Active", reservation.FindProperty(nameof(Reservation.Status))!
            .GetTypeMapping().Converter!.ConvertToProvider(ReservationStatus.Active));
    }

    [Fact]
    public void AuditEventModel_PreservesListingHistoryAndStoresActorAsScalar()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=luthrift_model_test")
            .Options;
        using var context = new ApplicationDbContext(options);

        var audit = context.Model.FindEntityType(typeof(AuditEvent));
        Assert.NotNull(audit);
        var primaryKey = audit.FindPrimaryKey();
        Assert.NotNull(primaryKey);
        var id = Assert.Single(primaryKey.Properties);
        Assert.Equal(nameof(AuditEvent.Id), id.Name);
        Assert.Equal(typeof(int), id.ClrType);

        var listingRelationship = Assert.Single(audit.GetForeignKeys());
        Assert.Equal(typeof(Listing), listingRelationship.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(AuditEvent.ListingId), Assert.Single(listingRelationship.Properties).Name);
        Assert.True(listingRelationship.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, listingRelationship.DeleteBehavior);

        var actorId = audit.FindProperty(nameof(AuditEvent.ActorUserId));
        Assert.NotNull(actorId);
        Assert.False(actorId.IsNullable);
        Assert.Equal(450, actorId.GetMaxLength());
        Assert.Empty(actorId.GetContainingForeignKeys());

        var action = audit.FindProperty(nameof(AuditEvent.Action));
        Assert.NotNull(action);
        Assert.False(action.IsNullable);
        Assert.Equal(50, action.GetMaxLength());

        var occurredAt = audit.FindProperty(nameof(AuditEvent.OccurredAtUtc));
        Assert.NotNull(occurredAt);
        Assert.False(occurredAt.IsNullable);
        Assert.Equal(typeof(DateTime), occurredAt.ClrType);
        Assert.Equal("timestamp with time zone", occurredAt.GetColumnType());

        var details = audit.FindProperty(nameof(AuditEvent.Details));
        Assert.NotNull(details);
        Assert.True(details.IsNullable);
        Assert.Equal(500, details.GetMaxLength());
    }

    [Fact]
    public async Task AuditHistory_SurvivesAccountDeletionAndArchivingButPreventsListingDeletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var actor = new ApplicationUser { Id = Guid.NewGuid().ToString(), DisplayName = "Audit test actor" };
        var listingId = await context.Listings.OrderBy(listing => listing.Id).Select(listing => listing.Id).FirstAsync();
        var audit = new AuditEvent
        {
            ListingId = listingId,
            ActorUserId = actor.Id,
            Action = "Test inventory edit"
        };
        context.Users.Add(actor);
        context.AuditEvents.Add(audit);
        await context.SaveChangesAsync();

        context.Users.Remove(actor);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.False(await context.Users.AnyAsync(user => user.Id == actor.Id));
        var history = await context.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(actor.Id, history.ActorUserId);

        // Leave the audit untracked so the database enforces the deletion restriction.
        var listingToDelete = await context.Listings.SingleAsync(listing => listing.Id == listingId);
        context.Listings.Remove(listingToDelete);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        var listingToArchive = await context.Listings.SingleAsync(listing => listing.Id == listingId);
        listingToArchive.Status = ListingStatus.Archived;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Equal(ListingStatus.Archived,
            (await context.Listings.AsNoTracking().SingleAsync(listing => listing.Id == listingId)).Status);
        var retainedHistory = await context.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(history.Id, retainedHistory.Id);
        Assert.Equal(listingId, retainedHistory.ListingId);
        Assert.Equal(actor.Id, retainedHistory.ActorUserId);
        Assert.Equal(history.Action, retainedHistory.Action);
        Assert.Equal(history.OccurredAtUtc, retainedHistory.OccurredAtUtc);
        Assert.Equal(history.Details, retainedHistory.Details);
    }
}
