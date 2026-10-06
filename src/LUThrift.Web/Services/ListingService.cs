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

    private static ListingResult<int>? ValidateWrite(object? input, string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId) || actorUserId.Length > 450)
        {
            return ListingResult<int>.Failure(ListingResultStatus.InvalidActor,
                new ValidationResult("A valid authenticated user ID is required to record this change."));
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
}
