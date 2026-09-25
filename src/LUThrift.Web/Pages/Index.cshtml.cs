using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Pages;

public class IndexModel(ApplicationDbContext db) : PageModel
{
    public IReadOnlyList<Listing> Listings { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Listings = await db.Listings
            .AsNoTracking()
            .Include(listing => listing.Category)
            .Where(listing => listing.Status == ListingStatus.Available)
            .OrderBy(listing => listing.Id)
            .ToListAsync(HttpContext.RequestAborted);
    }
}
