using System.Data.Common;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Services;

// Like ListingService, call only from staff-authorized application code. The actor
// comes from authentication, never a submitted user ID. No HttpContext dependency.
public sealed class ImageService(
    ApplicationDbContext context,
    IOptions<ImageStorageOptions> options,
    IWebHostEnvironment environment,
    ListingImageProcessor processor,
    TimeProvider timeProvider,
    ILogger<ImageService> logger)
{
    public async Task<ImageOperationResult> AddOrReplaceAsync(
        int listingId, Stream upload, string? actorUserId, Guid? expectedImageId = null,
        CancellationToken cancellationToken = default)
    {
        var invalidActor = ValidateActor(actorUserId);
        if (invalidActor is not null) return invalidActor;
        if (context.Database.CurrentTransaction is not null) return Conflict();
        var root = GetStorageRoot();
        if (root is null) return StorageUnavailable();

        ProcessedListingImage image;
        try
        {
            var processed = await processor.ProcessAsync(upload, cancellationToken);
            if (processed.Status != ImageProcessingStatus.Success)
                return processed.Status switch
                {
                    ImageProcessingStatus.TooLarge => new(ImageOperationStatus.TooLarge, "Choose a photo no larger than 5 MiB."),
                    ImageProcessingStatus.PixelLimitExceeded => new(ImageOperationStatus.PixelLimitExceeded,
                        "Choose a photo with at most 20 million pixels and no side longer than 8192 pixels."),
                    _ => new(ImageOperationStatus.InvalidImage, "Choose a complete, valid JPEG or PNG photo.")
                };
            image = processed.Image!;
        }
        catch (IOException)
        {
            return new(ImageOperationStatus.InvalidImage, "The uploaded photo could not be read. Choose it again.");
        }

        var imageId = Guid.NewGuid();
        var fileName = imageId.ToString("N") + image.Extension;
        var createdFile = false;
        var commitAttempted = false;
        var committed = false;
        ListingImage? metadata = null;
        AuditEvent? audit = null;
        try
        {
            Directory.CreateDirectory(root);
            // Immutable filenames: a competing request can never overwrite this
            // request's file. Record ownership before writing so partial writes clean up.
            await using (var file = new FileStream(Path.Combine(root, fileName), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                createdFile = true;
                await file.WriteAsync(image.Bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var ineligible = await LockEditableListingAsync(listingId, cancellationToken);
            if (ineligible is not null) return ineligible;

            var previous = await context.ListingImages.AsNoTracking()
                .SingleOrDefaultAsync(row => row.ListingId == listingId, cancellationToken);
            // Null means add only. A stale browser tab cannot overwrite a newer photo.
            if (previous?.Id != expectedImageId) return Conflict();
            if (previous is not null)
                await context.ListingImages.Where(row => row.Id == previous.Id).ExecuteDeleteAsync(cancellationToken);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            metadata = new ListingImage
            {
                Id = imageId, ListingId = listingId, StorageFileName = fileName,
                ContentType = image.ContentType, ByteLength = image.Bytes.LongLength,
                Width = image.Width, Height = image.Height, CreatedAtUtc = now
            };
            audit = NewAudit(listingId, actorUserId!, previous is null
                ? InventoryAuditActions.ImageAdded : InventoryAuditActions.ImageReplaced, now);
            context.ListingImages.Add(metadata);
            context.AuditEvents.Add(audit);
            await context.SaveChangesAsync(cancellationToken);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            committed = true;

            // Cleanup is after confirmed commit, and ignores request cancellation.
            // Its failure must not report an already-committed replacement as failed.
            var cleaned = previous is null || TryDelete(root, previous.StorageFileName);
            return new(ImageOperationStatus.Success, "Listing photo saved.", imageId, !cleaned);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return StorageUnavailable();
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError("A listing photo database change could not be confirmed. Refresh before retrying.");
            return new(ImageOperationStatus.PersistenceFailed, "The photo change could not be confirmed. Refresh before trying again.");
        }
        finally
        {
            // Detach only our entries; a later SaveChanges must not retry a failed write.
            if (metadata is not null) context.Entry(metadata).State = EntityState.Detached;
            if (audit is not null) context.Entry(audit).State = EntityState.Detached;
            if (createdFile && !committed)
            {
                if (!commitAttempted) TryDelete(root, fileName);
                else logger.LogWarning("A photo commit outcome is uncertain. Files were retained for reconciliation.");
            }
        }
    }

    public async Task<ImageOperationResult> RemoveAsync(
        int listingId, Guid expectedImageId, string? actorUserId, CancellationToken cancellationToken = default)
    {
        var invalidActor = ValidateActor(actorUserId);
        if (invalidActor is not null) return invalidActor;
        if (context.Database.CurrentTransaction is not null || expectedImageId == Guid.Empty) return Conflict();
        var root = GetStorageRoot();
        if (root is null) return StorageUnavailable();
        AuditEvent? audit = null;
        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var ineligible = await LockEditableListingAsync(listingId, cancellationToken);
            if (ineligible is not null) return ineligible;
            var previous = await context.ListingImages.AsNoTracking()
                .SingleOrDefaultAsync(row => row.ListingId == listingId, cancellationToken);
            if (previous is null || previous.Id != expectedImageId) return Conflict();

            await context.ListingImages.Where(row => row.Id == expectedImageId).ExecuteDeleteAsync(cancellationToken);
            audit = NewAudit(listingId, actorUserId!, InventoryAuditActions.ImageRemoved, timeProvider.GetUtcNow().UtcDateTime);
            context.AuditEvents.Add(audit);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var cleaned = TryDelete(root, previous.StorageFileName);
            return new(ImageOperationStatus.Success, "Listing photo removed.", CleanupPending: !cleaned);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError("A listing photo removal could not be confirmed. Refresh before retrying.");
            return new(ImageOperationStatus.PersistenceFailed, "The photo removal could not be confirmed. Refresh before trying again.");
        }
        finally
        {
            if (audit is not null) context.Entry(audit).State = EntityState.Detached;
        }
    }

    private async Task<ImageOperationResult?> LockEditableListingAsync(int listingId, CancellationToken cancellationToken)
    {
        // A same-value UPDATE obtains the listing write lock in PostgreSQL and
        // SQLite. Recheck status in the statement after any wait. No user locks are
        // acquired afterward, preserving reservation owner -> listing coordination.
        var count = await context.Listings.Where(listing => listing.Id == listingId
                && (listing.Status == ListingStatus.Draft || listing.Status == ListingStatus.Available
                    || listing.Status == ListingStatus.Missing))
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, listing => listing.Status), cancellationToken);
        if (count == 1) return null;
        return await context.Listings.AnyAsync(listing => listing.Id == listingId, cancellationToken)
            ? new(ImageOperationStatus.NotEditable, "This listing cannot be edited in its current status.")
            : new(ImageOperationStatus.NotFound, "The listing could not be found.");
    }

    private string? GetStorageRoot()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(options.Value.RootPath) || !Path.IsPathFullyQualified(options.Value.RootPath)) return null;
            var root = ResolveDirectory(options.Value.RootPath);
            var contentRoot = ResolveDirectory(environment.ContentRootPath);
            var repository = new DirectoryInfo(contentRoot);
            while (repository.Parent is not null && !Directory.Exists(Path.Combine(repository.FullName, ".git"))
                && !File.Exists(Path.Combine(repository.FullName, ".git"))) repository = repository.Parent;
            var repositoryRoot = repository.Parent is null ? contentRoot : repository.FullName;
            var webRoot = ResolveDirectory(environment.WebRootPath ?? Path.Combine(contentRoot, "wwwroot"));
            if (ContainsPath(repositoryRoot, root) || ContainsPath(root, repositoryRoot)
                || ContainsPath(contentRoot, root) || ContainsPath(webRoot, root)) return null;
            return root;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ResolveDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var resolved = Path.GetPathRoot(full)!;
        foreach (var segment in full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            var info = new DirectoryInfo(resolved);
            if (info.LinkTarget is not null) resolved = info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static bool ContainsPath(string directory, string candidate)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(directory, candidate, comparison)
            || candidate.StartsWith(Path.EndsInDirectorySeparator(directory)
                ? directory : directory + Path.DirectorySeparatorChar, comparison);
    }

    private bool TryDelete(string root, string fileName)
    {
        // Only ever delete this service's flat, generated filenames. Do not trust
        // even persisted metadata to introduce a path or delete an unrelated file.
        var extension = Path.GetExtension(fileName);
        if (extension is not (".jpg" or ".png") || fileName.Length != 36
            || !Guid.TryParseExact(fileName[..32], "N", out _))
        {
            logger.LogWarning("A listing photo file could not be cleaned up; inspect storage for unreferenced files.");
            return false;
        }
        try
        {
            File.Delete(Path.Combine(root, fileName));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("A listing photo file could not be cleaned up; inspect storage for unreferenced files.");
            return false;
        }
    }

    private static AuditEvent NewAudit(int listingId, string actor, string action, DateTime now) => new()
    {
        ListingId = listingId, ActorUserId = actor, Action = action, OccurredAtUtc = now,
        Details = "Optional listing photo changed."
    };

    private static ImageOperationResult? ValidateActor(string? actor)
        => string.IsNullOrWhiteSpace(actor) || actor.Length > 450
            ? new(ImageOperationStatus.InvalidActor, "A valid authenticated user ID is required to record this change.") : null;
    private static ImageOperationResult Conflict()
        => new(ImageOperationStatus.Conflict, "The listing photo changed. Refresh before trying again.");
    private static ImageOperationResult StorageUnavailable()
        => new(ImageOperationStatus.StorageUnavailable, "Photo storage is unavailable. Please try again later.");
}
