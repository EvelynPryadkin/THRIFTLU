using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

public class CatalogService(ApplicationDbContext context)
{
    public async Task<IReadOnlyList<CatalogListingSummary>> GetListingsAsync(
        CancellationToken cancellationToken = default)
        => await context.Listings.AsNoTracking()
            .Where(listing => listing.Status == ListingStatus.Available)
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
