using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

// Call from staff-authorized PageModels; actor IDs must come from the authenticated user, never form data.
public class ListingService(ApplicationDbContext context)
{
    private static readonly ListingStatus[] EditableStatuses =
        [ListingStatus.Draft, ListingStatus.Available, ListingStatus.Missing];

    public static bool IsEditable(ListingStatus status) => EditableStatuses.Contains(status);

    public static bool CanPublish(ListingStatus status) => status == ListingStatus.Draft;
    public static bool CanMarkMissing(ListingStatus status) => status == ListingStatus.Available;
    public static bool CanRecordWalkInCollection(ListingStatus status) => status == ListingStatus.Available;
    public static bool CanArchive(ListingStatus status)
        => status is ListingStatus.Draft or ListingStatus.Available or ListingStatus.Missing;

    public Task<ListingResult<int>> PublishAsync(
        int listingId, string? actorUserId, CancellationToken cancellationToken = default)
        => TransitionAsync(listingId, actorUserId, CanPublish, ListingStatus.Available,
            InventoryAuditActions.Published, cancellationToken);

    public Task<ListingResult<int>> MarkMissingAsync(
        int listingId, string? actorUserId, CancellationToken cancellationToken = default)
        => TransitionAsync(listingId, actorUserId, CanMarkMissing, ListingStatus.Missing,
            InventoryAuditActions.MarkedMissing, cancellationToken);

    public Task<ListingResult<int>> RecordWalkInCollectionAsync(
        int listingId, string? actorUserId, CancellationToken cancellationToken = default)
        => TransitionAsync(listingId, actorUserId, CanRecordWalkInCollection, ListingStatus.Collected,
            InventoryAuditActions.WalkInCollected, cancellationToken);

    public Task<ListingResult<int>> ArchiveAsync(
        int listingId, string? actorUserId, CancellationToken cancellationToken = default)
        => TransitionAsync(listingId, actorUserId, CanArchive, ListingStatus.Archived,
            InventoryAuditActions.Archived, cancellationToken);

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
        => await context.Categories.AsNoTracking()
            .OrderBy(category => category.DisplayOrder)
            .ThenBy(category => category.Name)
            .ThenBy(category => category.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Listing>> GetInventoryAsync(CancellationToken cancellationToken = default)
        => await context.Listings.AsNoTracking()
            .Include(listing => listing.Category)
            .OrderBy(listing => listing.Id)
            .ToListAsync(cancellationToken);

    public async Task<ListingResult<UpdateListingInput>> GetForEditAsync(
        int listingId, CancellationToken cancellationToken = default)
    {
        var listing = await context.Listings.AsNoTracking()
            .SingleOrDefaultAsync(listing => listing.Id == listingId, cancellationToken);
        if (listing is null)
        {
            return NotFound<UpdateListingInput>();
        }

        if (!IsEditable(listing.Status))
        {
            return NotEditable<UpdateListingInput>();
        }

        return ListingResult<UpdateListingInput>.Success(new UpdateListingInput
        {
            Title = listing.Title,
            Description = listing.Description,
            Condition = listing.Condition,
            Size = listing.Size,
            CategoryId = listing.CategoryId
        });
    }

    public async Task<ListingResult<int>> CreateAsync(
        CreateListingInput? input, string? actorUserId, CancellationToken cancellationToken = default)
    {
        var invalid = ValidateWrite(input, actorUserId);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!await context.Categories.AnyAsync(category => category.Id == input!.CategoryId, cancellationToken))
        {
            return CategoryNotFound();
        }

        var occurredAtUtc = DateTime.UtcNow;
        var listing = new Listing
        {
            Title = input!.Title.Trim(),
            Description = input.Description.Trim(),
            Condition = input.Condition.Trim(),
            Size = NormalizeSize(input.Size),
            CategoryId = input.CategoryId,
            Status = ListingStatus.Draft,
            CreatedAtUtc = occurredAtUtc
        };
        var audit = new AuditEvent
        {
            Listing = listing,
            ActorUserId = actorUserId!,
            Action = InventoryAuditActions.Created,
            OccurredAtUtc = occurredAtUtc
        };

        // EF saves the new listing and its audit event in one transaction.
        context.AuditEvents.Add(audit);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            context.Entry(audit).State = EntityState.Detached;
            context.Entry(listing).State = EntityState.Detached;
            throw;
        }

        return ListingResult<int>.Success(listing.Id);
    }

    public async Task<ListingResult<int>> UpdateAsync(
        int listingId, UpdateListingInput? input, string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var invalid = ValidateWrite(input, actorUserId);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!await context.Categories.AnyAsync(category => category.Id == input!.CategoryId, cancellationToken))
        {
            return CategoryNotFound();
        }

        var title = input!.Title.Trim();
        var description = input.Description.Trim();
        var condition = input.Condition.Trim();
        var size = NormalizeSize(input.Size);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Check eligibility in the UPDATE itself, so a stale edit cannot modify a now-held or terminal listing.
        var changed = await context.Listings
            .Where(listing => listing.Id == listingId && EditableStatuses.Contains(listing.Status))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(listing => listing.Title, title)
                .SetProperty(listing => listing.Description, description)
                .SetProperty(listing => listing.Condition, condition)
                .SetProperty(listing => listing.Size, size)
                .SetProperty(listing => listing.CategoryId, input.CategoryId), cancellationToken);

        if (changed == 0)
        {
            return await context.Listings.AnyAsync(listing => listing.Id == listingId, cancellationToken)
                ? NotEditable<int>()
                : NotFound<int>();
        }

        var audit = new AuditEvent
        {
            ListingId = listingId,
            ActorUserId = actorUserId!,
            Action = InventoryAuditActions.Updated,
            OccurredAtUtc = DateTime.UtcNow
        };
        context.AuditEvents.Add(audit);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            context.Entry(audit).State = EntityState.Detached;
            throw;
        }

        return ListingResult<int>.Success(listingId);
    }

    private async Task<ListingResult<int>> TransitionAsync(
        int listingId, string? actorUserId, Func<ListingStatus, bool> canTransition,
        ListingStatus nextStatus, string action, CancellationToken cancellationToken)
    {
        var invalidActor = ValidateActor(actorUserId);
        if (invalidActor is not null)
        {
            return invalidActor;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var currentStatus = await context.Listings.AsNoTracking()
            .Where(listing => listing.Id == listingId)
            .Select(listing => (ListingStatus?)listing.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (currentStatus is null)
        {
            return NotFound<int>();
        }

        if (!canTransition(currentStatus.Value))
        {
            return InvalidTransition();
        }

        // Check the status again in the UPDATE: another request may have changed it since the read.
        var changed = await context.Listings
            .Where(listing => listing.Id == listingId && listing.Status == currentStatus.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(listing => listing.Status, nextStatus),
                cancellationToken);
        if (changed == 0)
        {
            return await context.Listings.AnyAsync(listing => listing.Id == listingId, cancellationToken)
                ? InvalidTransition()
                : NotFound<int>();
        }

        var audit = new AuditEvent
        {
            ListingId = listingId,
            ActorUserId = actorUserId!,
            Action = action,
            OccurredAtUtc = DateTime.UtcNow,
            Details = $"Status changed from {currentStatus.Value} to {nextStatus}."
        };
        context.AuditEvents.Add(audit);
        try
        {
            // ExecuteUpdate and SaveChanges share this transaction; a failed audit rolls back the status.
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            context.Entry(audit).State = EntityState.Detached;
            throw;
        }

        return ListingResult<int>.Success(listingId);
    }

    private static ListingResult<int>? ValidateActor(string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId) || actorUserId.Length > 450)
        {
            return ListingResult<int>.Failure(ListingResultStatus.InvalidActor,
                new ValidationResult("A valid authenticated user ID is required to record this change."));
        }

        return null;
    }

    private static ListingResult<int>? ValidateWrite(object? input, string? actorUserId)
    {
        var invalidActor = ValidateActor(actorUserId);
        if (invalidActor is not null)
        {
            return invalidActor;
        }

        if (input is null)
        {
            return ListingResult<int>.Failure(ListingResultStatus.InvalidInput,
                new ValidationResult("Listing information is required."));
        }

        var errors = new List<ValidationResult>();
        return Validator.TryValidateObject(input, new ValidationContext(input), errors, validateAllProperties: true)
            ? null
            : ListingResult<int>.Failure(ListingResultStatus.InvalidInput, errors.ToArray());
    }

    private static string? NormalizeSize(string? size) => string.IsNullOrWhiteSpace(size) ? null : size.Trim();

    private static ListingResult<int> CategoryNotFound()
        => ListingResult<int>.Failure(ListingResultStatus.CategoryNotFound,
            new ValidationResult("The selected category no longer exists. Choose another category.",
                [nameof(CreateListingInput.CategoryId)]));

    private static ListingResult<T> NotFound<T>()
        => ListingResult<T>.Failure(ListingResultStatus.NotFound,
            new ValidationResult("The listing could not be found."));

    private static ListingResult<T> NotEditable<T>()
        => ListingResult<T>.Failure(ListingResultStatus.NotEditable,
            new ValidationResult("Only draft, available, or missing listings can be edited."));

    private static ListingResult<int> InvalidTransition()
        => ListingResult<int>.Failure(ListingResultStatus.InvalidTransition,
            new ValidationResult("This action is not available for the listing's current status. Refresh the inventory and try again."));
}
