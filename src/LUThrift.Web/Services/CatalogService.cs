using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

public class CatalogService(ApplicationDbContext context)
{
    public Task<IReadOnlyList<CatalogListingSummary>> GetListingsAsync(
        CancellationToken cancellationToken = default)
        => GetListingsAsync(new CatalogFilterInput(), cancellationToken);

    public async Task<IReadOnlyList<CatalogListingSummary>> GetListingsAsync(
        CatalogFilterInput filter, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validator.TryValidateObject(filter, new ValidationContext(filter), null, validateAllProperties: true))
        {
            return [];
        }

        // Every filter starts within the public catalog, including the "All" option.
        var query = context.Listings.AsNoTracking()
            .Where(listing => listing.Status == ListingStatus.Available || listing.Status == ListingStatus.Held);

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

        return await query
            .OrderByDescending(listing => listing.CreatedAtUtc)
            .ThenByDescending(listing => listing.Id)
            .Take(12)
            .Select(listing => new CatalogListingSummary(
                listing.Id,
                listing.Title,
                listing.Category.Name,
                listing.Condition,
                listing.Size,
                listing.Status))
            .ToListAsync(cancellationToken);
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
        => await context.Listings.AsNoTracking()
            // Apply visibility even for a direct ID lookup; a known ID is not permission to see a listing.
            .Where(listing => listing.Id == listingId
                && (listing.Status == ListingStatus.Available || listing.Status == ListingStatus.Held))
            .Select(listing => new CatalogListingDetails(
                listing.Id,
                listing.Title,
                listing.Description,
                listing.Category.Name,
                listing.Condition,
                listing.Size,
                listing.Status))
            .SingleOrDefaultAsync(cancellationToken);
}
