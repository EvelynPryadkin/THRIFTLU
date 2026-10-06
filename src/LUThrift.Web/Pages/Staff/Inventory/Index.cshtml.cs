using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Staff.Inventory;

public class IndexModel(ListingService listingService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    [EnumDataType(typeof(ListingStatus))]
    public ListingStatus? Status { get; set; }

    public IReadOnlyList<Listing> Listings { get; private set; } = [];
    public bool HasInventory { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest("Choose a valid inventory status.");
        }

        var inventory = await listingService.GetInventoryAsync(cancellationToken);
        HasInventory = inventory.Count > 0;
        Listings = inventory
            .Where(listing => !Status.HasValue || listing.Status == Status.Value)
            .OrderByDescending(listing => listing.CreatedAtUtc)
            .ThenByDescending(listing => listing.Id)
            .ToArray();

        return Page();
    }
}
