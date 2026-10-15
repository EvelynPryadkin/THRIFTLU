using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Thrift;

public class DetailsModel(CatalogService catalogService) : PageModel
{
    public CatalogListingDetails Listing { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        var listing = await catalogService.GetDetailsAsync(id, cancellationToken);
        if (listing is null)
        {
            return NotFound();
        }

        Listing = listing;
        return Page();
    }
}
