using System.Data.Common;
using System.Security.Claims;
using LUThrift.Web.Authorization;
using Microsoft.AspNetCore.Identity;
using System.Text.Json;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace LUThrift.Tests.Integration;

public sealed class ImageUploadTests
{
    private const string ActorId = "synthetic-image-editor";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 16, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg", ".jpg")]
    [InlineData(SKEncodedImageFormat.Png, "image/png", ".png")]
    public async Task Add_StoresOneDecodedPhotoWithGeneratedNameAndAudit(
        SKEncodedImageFormat format, string contentType, string extension)
    {
        await using var test = await StorageTest.CreateAsync();
        var before = await test.InventoryAndReservationsAsync();
        Assert.Empty(await test.Database.ListingImages.ToListAsync());
        using var upload = Image(format);

        var result = await test.Service.AddOrReplaceAsync(1, upload, Staff());

        Assert.True(result.Succeeded);
        Assert.False(result.CleanupPending);
        var metadata = await test.PhotoAsync();
        Assert.Equal(result.ImageId, metadata.Id);
        Assert.NotEqual(Guid.Empty, metadata.Id);
        Assert.Equal(1, metadata.ListingId);
        Assert.Equal(contentType, metadata.ContentType);
        Assert.Equal(Now.UtcDateTime, metadata.CreatedAtUtc);
        Assert.Equal((7, 5), (metadata.Width, metadata.Height));
        Assert.Matches("^[a-f0-9]{32}\\" + extension + "$", metadata.StorageFileName);
        var storedPath = Path.Combine(test.StorageRoot, metadata.StorageFileName);
        Assert.Equal(storedPath, Assert.Single(test.Files()));
        var stored = await File.ReadAllBytesAsync(storedPath);
        Assert.Equal(stored.LongLength, metadata.ByteLength);
        using var decoded = SKBitmap.Decode(stored);
        Assert.NotNull(decoded);
        Assert.Equal((7, 5), (decoded.Width, decoded.Height));
        var audit = Assert.Single(await test.AuditsAsync());
        Assert.Equal(InventoryAuditActions.ImageAdded, audit.Action);
        Assert.Equal(ActorId, audit.ActorUserId);
        Assert.Equal(1, audit.ListingId);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAtUtc);
        Assert.Equal(before, await test.InventoryAndReservationsAsync());
    }

    [Fact]
    public async Task ReplaceAndRemove_RetainListingAndReservationHistoryAndCleanOnlySupersededFiles()
    {
        await using var test = await StorageTest.CreateAsync();
        var original = await test.AddAsync();
        var oldFile = Path.Combine(test.StorageRoot, original.StorageFileName);
        var inventory = await test.InventoryAndReservationsAsync();
        using var replacement = Image(SKEncodedImageFormat.Jpeg, 11, 9);

        var result = await test.Service.AddOrReplaceAsync(1, replacement, Staff(), original.Id);

        Assert.True(result.Succeeded);
        var updated = await test.PhotoAsync();
        Assert.NotEqual(original.Id, updated.Id);
        Assert.NotEqual(original.StorageFileName, updated.StorageFileName);
        Assert.Equal((11, 9), (updated.Width, updated.Height));
        Assert.False(File.Exists(oldFile));
        Assert.Equal(Path.Combine(test.StorageRoot, updated.StorageFileName), Assert.Single(test.Files()));
        var removed = await test.Service.RemoveAsync(1, updated.Id, Staff());
        Assert.True(removed.Succeeded);
        Assert.Empty(await test.Database.ListingImages.AsNoTracking().ToListAsync());
        Assert.Empty(test.Files());
        Assert.Equal(new[] { InventoryAuditActions.ImageAdded, InventoryAuditActions.ImageReplaced, InventoryAuditActions.ImageRemoved },
            (await test.AuditsAsync()).Select(row => row.Action));
        Assert.Equal(inventory, await test.InventoryAndReservationsAsync());

        var repeated = await test.Service.RemoveAsync(1, updated.Id, Staff());
        Assert.Equal(ImageOperationStatus.Conflict, repeated.Status);
        Assert.Equal(3, (await test.AuditsAsync()).Length);
    }

    [Theory]
    [InlineData(ListingStatus.Draft)]
    [InlineData(ListingStatus.Available)]
    [InlineData(ListingStatus.Missing)]
    public async Task PhotoChanges_PreserveEditableListingStatus(ListingStatus status)
    {
        await using var test = await StorageTest.CreateAsync();
        await test.Database.Listings.Where(row => row.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, status));
        var before = await test.InventoryAndReservationsAsync();
        var image = await test.AddAsync();
        Assert.True((await test.Service.RemoveAsync(1, image.Id, Staff())).Succeeded);
        Assert.Equal(before, await test.InventoryAndReservationsAsync());
    }

    [Theory]
    [InlineData(ListingStatus.Held)]
    [InlineData(ListingStatus.Collected)]
    [InlineData(ListingStatus.Archived)]
    public async Task StalePhotoChanges_CannotAlterHeldOrTerminalListings(ListingStatus status)
    {
        await using var test = await StorageTest.CreateAsync();
        var original = await test.AddAsync();
        await test.Database.Listings.Where(row => row.Id == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, status));
        var before = await test.StateAsync();
        using var upload = Image();

        Assert.Equal(ImageOperationStatus.NotEditable,
            (await test.Service.AddOrReplaceAsync(1, upload, Staff(), original.Id)).Status);
        Assert.Equal(ImageOperationStatus.NotEditable,
            (await test.Service.RemoveAsync(1, original.Id, Staff())).Status);
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(Path.Combine(test.StorageRoot, original.StorageFileName), Assert.Single(test.Files()));
    }

    [Fact]
    public async Task UnknownListing_RejectsWritesAndCleansCandidateFile()
    {
        await using var test = await StorageTest.CreateAsync();
        var before = await test.StateAsync();
        using var upload = Image();
        Assert.Equal(ImageOperationStatus.NotFound,
            (await test.Service.AddOrReplaceAsync(int.MaxValue, upload, Staff())).Status);
        Assert.Equal(ImageOperationStatus.NotFound,
            (await test.Service.RemoveAsync(int.MaxValue, Guid.NewGuid(), Staff())).Status);
        Assert.Equal(before, await test.StateAsync());
        Assert.Empty(test.Files());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingActor_CannotWritePhotoOrAudit(string? actorId)
    {
        await using var test = await StorageTest.CreateAsync();
        using var upload = Image();
        Assert.Equal(ImageOperationStatus.AuthenticationRequired,
            (await test.Service.AddOrReplaceAsync(1, upload, Staff(actorId))).Status);
        Assert.Equal(ImageOperationStatus.AuthenticationRequired,
            (await test.Service.RemoveAsync(1, Guid.NewGuid(), Staff(actorId))).Status);
        Assert.Empty(await test.AuditsAsync());
        Assert.Empty(test.Files());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("student")]
    public async Task Service_RechecksCurrentAccountInsteadOfTrustingStaffClaims(string change)
    {
        await using var test = await StorageTest.CreateAsync();
        var original = await test.AddAsync();
        var principal = Staff(); // Retains a Staff claim after the database changes.
        if (change == "inactive")
            await test.Database.Users.Where(row => row.Id == ActorId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        else if (change == "deleted")
            await test.Database.Users.Where(row => row.Id == ActorId).ExecuteDeleteAsync();
        else
        {
            await test.Database.UserRoles.Where(row => row.UserId == ActorId).ExecuteDeleteAsync();
            if (change == "student")
            {
                test.Database.Roles.Add(new IdentityRole(ApplicationRoles.Student) { Id = "image-student-role" });
                test.Database.UserRoles.Add(new IdentityUserRole<string> { UserId = ActorId, RoleId = "image-student-role" });
                await test.Database.SaveChangesAsync();
            }
        }
        var before = await test.StateAsync();
        var files = test.Files();
        using var upload = Image();
        Assert.Equal(ImageOperationStatus.StaffRequired,
            (await test.Service.AddOrReplaceAsync(1, upload, principal, original.Id)).Status);
        Assert.Equal(0, upload.Position);
        Assert.Equal(ImageOperationStatus.StaffRequired,
            (await test.Service.RemoveAsync(1, original.Id, principal)).Status);
        Assert.Equal(ImageOperationStatus.StaffRequired,
            (await test.Service.GetForEditAsync(1, principal)).Status);
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(files, test.Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidReplacement_PreservesPreviousPhotoAndAudit(bool oversized)
    {
        await using var test = await StorageTest.CreateAsync();
        var original = await test.AddAsync();
        var before = await test.StateAsync();
        var oldBytes = await File.ReadAllBytesAsync(Path.Combine(test.StorageRoot, original.StorageFileName));
        using var upload = new MemoryStream(oversized
            ? new byte[ListingImageProcessor.MaximumUploadBytes + 1]
            : "<script>This is not a JPEG</script>"u8.ToArray());

        var result = await test.Service.AddOrReplaceAsync(1, upload, Staff(), original.Id);

        Assert.Equal(oversized ? ImageOperationStatus.TooLarge : ImageOperationStatus.InvalidImage, result.Status);
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(Assert.Single(test.Files())));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    [InlineData("remove")]
    public async Task AuditSaveFailure_RollsBackMetadataAndFilesWithoutLaterRetry(string operation)
    {
        await using var test = await StorageTest.CreateAsync();
        var original = operation == "add" ? null : await test.AddAsync();
        var before = await test.StateAsync();
        var files = test.Files();
        var oldBytes = original is null ? null : await File.ReadAllBytesAsync(Assert.Single(files));
        await test.Database.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectImageAudit BEFORE INSERT ON "AuditEvents"
            BEGIN SELECT RAISE(ABORT, 'Synthetic image audit failure'); END;
            """);
        using var upload = Image(SKEncodedImageFormat.Jpeg, 13, 6);

        var result = operation == "remove"
            ? await test.Service.RemoveAsync(1, original!.Id, Staff())
            : await test.Service.AddOrReplaceAsync(1, upload, Staff(), original?.Id);

        Assert.Equal(ImageOperationStatus.PersistenceFailed, result.Status);
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(files, test.Files());
        if (oldBytes is not null) Assert.Equal(oldBytes, await File.ReadAllBytesAsync(Assert.Single(files)));
        await test.Database.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectImageAudit;");
        await test.Database.SaveChangesAsync();
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(files, test.Files());
    }

    [Fact]
    public async Task LostCommitAcknowledgement_RetainsBothFilesAndTheCommittedReplacement()
    {
        var failure = new LostCommitAcknowledgementInterceptor();
        await using var test = await StorageTest.CreateAsync(interceptors: [failure]);
        var original = await test.AddAsync();
        var oldFile = Assert.Single(test.Files());
        var oldBytes = await File.ReadAllBytesAsync(oldFile);
        var inventory = await test.InventoryAndReservationsAsync();
        using var upload = Image(SKEncodedImageFormat.Jpeg, 13, 6);
        failure.Enabled = true;

        var result = await test.Service.AddOrReplaceAsync(1, upload, Staff(), original.Id);

        Assert.True(failure.Triggered);
        Assert.Equal(ImageOperationStatus.PersistenceFailed, result.Status);
        Assert.False(result.Succeeded);
        // The database did commit before the acknowledgement was lost. Destructive
        // compensation here would leave its new metadata pointing at a missing file.
        var replacement = await test.PhotoAsync();
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal((13, 6), (replacement.Width, replacement.Height));
        var newFile = Path.Combine(test.StorageRoot, replacement.StorageFileName);
        Assert.Equal(2, test.Files().Length);
        Assert.Contains(oldFile, test.Files());
        Assert.Contains(newFile, test.Files());
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldFile));
        using var decoded = SKBitmap.Decode(await File.ReadAllBytesAsync(newFile));
        Assert.NotNull(decoded);
        Assert.Equal((13, 6), (decoded.Width, decoded.Height));
        var audits = await test.AuditsAsync();
        Assert.Equal(new[] { InventoryAuditActions.ImageAdded, InventoryAuditActions.ImageReplaced },
            audits.Select(row => row.Action));
        Assert.Equal(ActorId, audits[1].ActorUserId);
        Assert.Equal(Now.UtcDateTime, audits[1].OccurredAtUtc);
        Assert.Equal(inventory, await test.InventoryAndReservationsAsync());

        var committedState = await test.StateAsync();
        failure.Enabled = false;
        await test.Database.SaveChangesAsync();
        Assert.Equal(committedState, await test.StateAsync());
        Assert.Equal(2, test.Files().Length);
    }

    [Fact]
    public async Task CancellationBeforeSave_RollsBackReplacementAndCleansCandidateWithoutLaterRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new CancelImageSaveInterceptor(cancellation);
        await using var test = await StorageTest.CreateAsync(interceptors: [failure]);
        var original = await test.AddAsync();
        var before = await test.StateAsync();
        var oldFile = Assert.Single(test.Files());
        var oldBytes = await File.ReadAllBytesAsync(oldFile);
        failure.BeforeCancellation = () =>
        {
            // Exercise cancellation after the candidate was written and the old
            // metadata deleted inside the transaction, not a pre-canceled upload.
            Assert.Equal(2, test.Files().Length);
            Assert.Single(test.Database.ChangeTracker.Entries<ListingImage>(),
                entry => entry.State == EntityState.Added);
            Assert.Single(test.Database.ChangeTracker.Entries<AuditEvent>(),
                entry => entry.State == EntityState.Added);
        };
        failure.Enabled = true;
        using var upload = Image(SKEncodedImageFormat.Jpeg, 13, 6);

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.AddOrReplaceAsync(1, upload, Staff(), original.Id, cancellation.Token));

        Assert.True(failure.Triggered);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(oldFile, Assert.Single(test.Files()));
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldFile));
        failure.Enabled = false;
        await test.Database.SaveChangesAsync();
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(oldFile, Assert.Single(test.Files()));
    }

    [Fact]
    public async Task CompetingEditsWithSameImageId_OnlyFirstSucceedsAndStaleRemoveCannotDeleteWinner()
    {
        await using var test = await StorageTest.CreateAsync();
        var original = await test.AddAsync();
        // Two scopes represent forms opened on the same original image.
        await using var firstContext = new ApplicationDbContext(test.Options);
        await using var staleContext = new ApplicationDbContext(test.Options);
        var first = test.CreateService(firstContext);
        var stale = test.CreateService(staleContext);
        using var firstUpload = Image(SKEncodedImageFormat.Jpeg, 8, 4);
        using var staleUpload = Image(SKEncodedImageFormat.Png, 4, 8);

        var winner = await first.AddOrReplaceAsync(1, firstUpload, Staff(), original.Id);
        Assert.True(winner.Succeeded);
        var committed = await test.StateAsync();
        var winnerFile = Assert.Single(test.Files());
        var bytes = await File.ReadAllBytesAsync(winnerFile);
        Assert.Equal(ImageOperationStatus.Conflict,
            (await stale.AddOrReplaceAsync(1, staleUpload, Staff(), original.Id)).Status);
        Assert.Equal(ImageOperationStatus.Conflict,
            (await stale.RemoveAsync(1, original.Id, Staff())).Status);
        Assert.Equal(committed, await test.StateAsync());
        Assert.Equal(winnerFile, Assert.Single(test.Files()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(winnerFile));
        Assert.Equal(2, (await test.AuditsAsync()).Length);
    }

    [Fact]
    public async Task SimultaneousReplacements_OnSeparateConnections_CommitOnePhotoAndCleanLosingCandidate()
    {
        await using var test = await StorageTest.CreateAsync(fileDatabase: true);
        var original = await test.AddAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new CountdownEvent(2);
        async Task<ImageOperationResult> ReplaceAsync(int width, string actor)
        {
            await using var context = new ApplicationDbContext(test.Options);
            using var upload = Image(SKEncodedImageFormat.Png, width, 5);
            ready.Signal();
            await start.Task;
            return await test.CreateService(context).AddOrReplaceAsync(1, upload, Staff(actor), original.Id);
        }
        var first = Task.Run(() => ReplaceAsync(8, "synthetic-first-editor"));
        var second = Task.Run(() => ReplaceAsync(9, "synthetic-second-editor"));
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        var success = Assert.Single(results, row => row.Succeeded);
        Assert.Equal(ImageOperationStatus.Conflict, Assert.Single(results, row => !row.Succeeded).Status);
        var image = await test.PhotoAsync();
        Assert.Equal(success.ImageId, image.Id);
        Assert.Equal(Path.Combine(test.StorageRoot, image.StorageFileName), Assert.Single(test.Files()));
        Assert.False(File.Exists(Path.Combine(test.StorageRoot, original.StorageFileName)));
        var audits = await test.AuditsAsync();
        Assert.Equal(2, audits.Length);
        Assert.Equal(InventoryAuditActions.ImageReplaced, audits[1].Action);
        Assert.Equal(results[0].Succeeded ? "synthetic-first-editor" : "synthetic-second-editor", audits[1].ActorUserId);
    }

    [Fact]
    public async Task CleanupFailureAfterCommit_ReportsSuccessAndRetainsNewReferencedPhoto()
    {
        await using var test = await StorageTest.CreateAsync();
        var original = await test.AddAsync();
        var oldPath = Path.Combine(test.StorageRoot, original.StorageFileName);
        // A directory occupying the old filename makes File.Delete fail without
        // changing directory permissions or depending on the test process's user.
        File.Delete(oldPath);
        Directory.CreateDirectory(oldPath);
        using var upload = Image(SKEncodedImageFormat.Jpeg);

        var result = await test.Service.AddOrReplaceAsync(1, upload, Staff(), original.Id);

        Assert.True(result.Succeeded);
        Assert.True(result.CleanupPending);
        var replacement = await test.PhotoAsync();
        Assert.Equal(result.ImageId, replacement.Id);
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.True(File.Exists(Path.Combine(test.StorageRoot, replacement.StorageFileName)));
        Assert.True(Directory.Exists(oldPath));
        Assert.Equal(2, (await test.AuditsAsync()).Length);
    }

    [Fact]
    public async Task AddWithoutExpectedId_CannotOverwriteExistingPhoto()
    {
        await using var test = await StorageTest.CreateAsync();
        await test.AddAsync();
        var before = await test.StateAsync();
        var files = test.Files();
        using var upload = Image();
        Assert.Equal(ImageOperationStatus.Conflict,
            (await test.Service.AddOrReplaceAsync(1, upload, Staff())).Status);
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(files, test.Files());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("repository")]
    [InlineData("webroot")]
    [InlineData("file")]
    [InlineData("symlink")]
    public async Task InvalidOrUnavailableStorage_RejectsWritesWithoutChangingPreviousPhoto(string mode)
    {
        await using var test = await StorageTest.CreateAsync();
        var original = await test.AddAsync();
        var before = await test.StateAsync();
        var files = test.Files();
        var link = Path.Combine(test.WorkRoot, "linked-repository");
        if (mode == "symlink") Directory.CreateSymbolicLink(link, test.Environment.ContentRootPath);
        var blocker = Path.Combine(test.WorkRoot, "not-a-directory");
        if (mode == "file") await File.WriteAllTextAsync(blocker, "synthetic blocker");
        var root = mode switch
        {
            "missing" => null,
            "relative" => "relative-storage",
            "repository" => test.Environment.ContentRootPath,
            "webroot" => test.Environment.WebRootPath,
            "file" => blocker,
            "symlink" => Path.Combine(link, "images"),
            _ => throw new InvalidOperationException()
        };
        var service = test.CreateService(test.Database, root);
        using var upload = Image();

        Assert.Equal(ImageOperationStatus.StorageUnavailable,
            (await service.AddOrReplaceAsync(1, upload, Staff(), original.Id)).Status);
        Assert.Equal(before, await test.StateAsync());
        Assert.Equal(files, test.Files());
    }

    private static ClaimsPrincipal Staff(string? actorId = ActorId)
        => new(new ClaimsIdentity(actorId is null ? [] :
            [new Claim(ClaimTypes.NameIdentifier, actorId), new Claim(ClaimTypes.Role, ApplicationRoles.Staff)],
            actorId is null ? null : "Image test"));

    private static MemoryStream Image(SKEncodedImageFormat format = SKEncodedImageFormat.Png, int width = 7, int height = 5)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return new MemoryStream(data.ToArray(), writable: false);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LostCommitAcknowledgementInterceptor : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!Enabled) return Task.CompletedTask;
            Triggered = true;
            throw new SyntheticCommitAcknowledgementException();
        }
    }

    private sealed class SyntheticCommitAcknowledgementException : DbException
    {
        public SyntheticCommitAcknowledgementException() : base("Synthetic lost commit acknowledgement.") { }
    }

    private sealed class CancelImageSaveInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public Action? BeforeCancellation { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Enabled) return new(result);
            Triggered = true;
            BeforeCancellation?.Invoke();
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "LUThrift.Web";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StorageTest(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options,
        ApplicationDbContext database, string workRoot) : IAsyncDisposable
    {
        public DbContextOptions<ApplicationDbContext> Options { get; } = options;
        public ApplicationDbContext Database { get; } = database;
        public string WorkRoot { get; } = workRoot;
        public string StorageRoot => Path.Combine(WorkRoot, "images");
        public TestEnvironment Environment { get; } = new();
        public ImageService Service => CreateService(Database);
        public ImageService CreateService(ApplicationDbContext context)
            => CreateService(context, StorageRoot);
        public ImageService CreateService(ApplicationDbContext context, string? storageRoot)
            => new(context, Microsoft.Extensions.Options.Options.Create(new ImageStorageOptions { RootPath = storageRoot }),
                Environment, new ListingImageProcessor(), new FixedClock(), NullLogger<ImageService>.Instance);

        public static async Task<StorageTest> CreateAsync(bool fileDatabase = false, params IInterceptor[] interceptors)
        {
            var workRoot = Path.Combine(Path.GetTempPath(), "luthrift-images-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workRoot);
            var connectionString = fileDatabase
                ? "Data Source=" + Path.Combine(workRoot, "images.sqlite") + ";Foreign Keys=True;Pooling=False;Default Timeout=10"
                : "Data Source=:memory:;Foreign Keys=True";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
            builder.AddInterceptors(interceptors);
            var options = (fileDatabase ? builder.UseSqlite(connectionString) : builder.UseSqlite(connection)).Options;
            var database = new ApplicationDbContext(options);
            await database.Database.EnsureCreatedAsync();
            var student = new ApplicationUser { Id = "image-test-student", DisplayName = "Synthetic image student" };
            var staffRole = new IdentityRole(ApplicationRoles.Staff) { Id = "image-test-staff-role", NormalizedName = "STAFF" };
            database.Roles.Add(staffRole);
            foreach (var staffId in new[] { ActorId, "synthetic-first-editor", "synthetic-second-editor" })
            {
                database.Users.Add(new ApplicationUser { Id = staffId, DisplayName = "Synthetic image editor", IsActive = true });
                database.UserRoles.Add(new IdentityUserRole<string> { UserId = staffId, RoleId = staffRole.Id });
            }
            database.Users.Add(student);
            database.Reservations.Add(new Reservation
            {
                User = student, ListingId = 1, Status = ReservationStatus.Canceled,
                CreatedAtUtc = Now.AddDays(-1).UtcDateTime, ExpiresAtUtc = Now.AddDays(-1).AddHours(1).UtcDateTime
            });
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            return new(connection, options, database, workRoot);
        }
        public async Task<ListingImage> AddAsync()
        {
            using var upload = Image();
            Assert.True((await Service.AddOrReplaceAsync(1, upload, Staff())).Succeeded);
            return await PhotoAsync();
        }
        public Task<ListingImage> PhotoAsync() => Database.ListingImages.AsNoTracking().SingleAsync();
        public Task<AuditEvent[]> AuditsAsync() => Database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        public string[] Files() => Directory.Exists(StorageRoot)
            ? Directory.GetFiles(StorageRoot, "*", SearchOption.AllDirectories).OrderBy(file => file).ToArray() : [];
        public async Task<string> InventoryAndReservationsAsync() => JsonSerializer.Serialize(new
        {
            Listings = await Database.Listings.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Title, row.Description, row.Condition, row.Size, row.CategoryId, row.Status, row.CreatedAtUtc }).ToArrayAsync(),
            Reservations = await Database.Reservations.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.UserId, row.ListingId, row.Status, row.CreatedAtUtc, row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason }).ToArrayAsync()
        });
        public async Task<string> StateAsync() => JsonSerializer.Serialize(new
        {
            Inventory = await InventoryAndReservationsAsync(),
            Images = await Database.ListingImages.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.ListingId, row.StorageFileName, row.ContentType, row.Width, row.Height, row.ByteLength, row.CreatedAtUtc }).ToArrayAsync(),
            Audits = (await AuditsAsync()).Select(row => new { row.Id, row.ListingId, row.ActorUserId, row.Action, row.OccurredAtUtc, row.Details })
        });
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
            Directory.Delete(WorkRoot, recursive: true);
        }
    }
}
