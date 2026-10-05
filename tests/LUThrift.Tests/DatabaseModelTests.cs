using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Tests;

public class DatabaseModelTests
{
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
