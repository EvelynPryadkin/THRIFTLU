using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

public class CatalogService(ApplicationDbContext context, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<IReadOnlyList<CatalogListingSummary>> GetListingsAsync(
        CancellationToken cancellationToken = default)
        => GetListingsAsync(new CatalogFilterInput(), cancellationToken);

    public async Task<IReadOnlyList<CatalogListingSummary>> GetListingsAsync(
        CatalogFilterInput filter, CancellationToken cancellationToken = default)
        => (await GetPageAsync(filter, cancellationToken: cancellationToken)).Items;

    public async Task<CatalogListingPage> GetPageAsync(
        CatalogFilterInput filter, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validator.TryValidateObject(filter, new ValidationContext(filter), null, validateAllProperties: true))
        {
            return new CatalogListingPage([], 0, 1);
        }

        // The same captured cutoff is used by filtering, counting, and projection.
        var query = PublicListingsAt(clock.GetUtcNow().UtcDateTime);

        if (filter.Availability != CatalogAvailability.All)
        {
            var status = filter.Availability == CatalogAvailability.Reserved
                ? ListingStatus.Held : ListingStatus.Available;
            query = query.Where(listing => listing.Status == status);
        }

        if (filter.Search is { } search)
        {
            // Escape LIKE metacharacters so the input remains literal substring text.
            var pattern = "%" + search.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
            if (context.Database.IsNpgsql())
            {
                query = query.Where(listing => EF.Functions.ILike(listing.Title, pattern, "!"));
            }
            else
            {
                // SQLite has no ILIKE; its lower/LIKE behavior is covered separately from PostgreSQL.
                var lowercasePattern = pattern.ToLowerInvariant();
                query = query.Where(listing => EF.Functions.Like(listing.Title.ToLower(), lowercasePattern, "!"));
            }
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(listing => listing.CategoryId == categoryId);
        }

        if (filter.Size is { } size)
        {
            query = query.Where(listing => listing.Size == size);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 1 : (totalCount - 1) / CatalogListingPage.PageSize + 1;
        var currentPage = Math.Clamp(pageNumber, 1, totalPages);
        // Clamp before multiplying, so even int.MaxValue cannot overflow the offset.
        var offset = (currentPage - 1) * CatalogListingPage.PageSize;

        var items = await query
            .OrderByDescending(listing => listing.CreatedAtUtc)
            .ThenByDescending(listing => listing.Id)
            .Skip(offset)
            .Take(CatalogListingPage.PageSize)
            .Select(listing => new CatalogListingSummary(
                listing.Id,
                listing.Title,
                listing.CategoryName,
                listing.Condition,
                listing.Size,
                listing.Status))
            .ToListAsync(cancellationToken);

        return new CatalogListingPage(items, totalCount, currentPage);
    }

    public async Task<IReadOnlyList<CatalogCategoryOption>> GetCategoriesAsync(
        CancellationToken cancellationToken = default)
        => await context.Categories.AsNoTracking()
            .OrderBy(category => category.DisplayOrder)
            .ThenBy(category => category.Name)
            .ThenBy(category => category.Id)
            .Select(category => new CatalogCategoryOption(category.Id, category.Name))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> GetSizesAsync(CancellationToken cancellationToken = default)
        => await context.Listings.AsNoTracking()
            .Where(listing => (listing.Status == ListingStatus.Available || listing.Status == ListingStatus.Held)
                && listing.Size != null && listing.Size.Trim() != "")
            .Select(listing => listing.Size!)
            .Distinct()
            .OrderBy(size => size)
            .ToListAsync(cancellationToken);

    public async Task<CatalogListingDetails?> GetDetailsAsync(
        int listingId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await PublicListingsAt(clock.GetUtcNow().UtcDateTime)
            .Where(listing => listing.Id == listingId)
            .Select(listing => new CatalogListingDetails(
                listing.Id,
                listing.Title,
                listing.Description,
                listing.CategoryName,
                listing.Condition,
                listing.Size,
                listing.Status))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private IQueryable<PublicListing> PublicListingsAt(DateTime nowUtc)
        => context.Listings.AsNoTracking()
            // Stored private statuses stay private, including direct detail lookups.
            .Where(listing => listing.Status == ListingStatus.Available || listing.Status == ListingStatus.Held)
            .Select(listing => new PublicListing
            {
                Id = listing.Id,
                Title = listing.Title,
                Description = listing.Description,
                CategoryId = listing.CategoryId,
                CategoryName = listing.Category.Name,
                Condition = listing.Condition,
                Size = listing.Size,
                CreatedAtUtc = listing.CreatedAtUtc,
                // Display known expiration without writing during a public GET. An orphan hold or
                // newer reservation history must not be mistaken for this expired reservation's hold.
                Status = listing.Status == ListingStatus.Held
                    && listing.Reservations.Any(reservation => reservation.Status == ReservationStatus.Active
                        && reservation.ExpiresAtUtc <= nowUtc
                        && !listing.Reservations.Any(other => other.Id != reservation.Id
                            && other.Status == ReservationStatus.Active)
                        && !listing.Reservations.Any(newer => newer.CreatedAtUtc > reservation.CreatedAtUtc
                            || newer.CreatedAtUtc == reservation.CreatedAtUtc && newer.Id > reservation.Id))
                    ? ListingStatus.Available : listing.Status
            });

    private sealed class PublicListing
    {
        public int Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public int CategoryId { get; init; }
        public string CategoryName { get; init; } = string.Empty;
        public string Condition { get; init; } = string.Empty;
        public string? Size { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public ListingStatus Status { get; init; }
    }
}
