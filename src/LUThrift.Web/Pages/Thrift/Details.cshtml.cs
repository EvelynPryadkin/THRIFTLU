using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Thrift;

public class DetailsModel(CatalogService catalogService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public CatalogFilterInput Filter { get; set; } = new();

    public CatalogListingDetails Listing { get; private set; } = null!;
    public Dictionary<string, string> CatalogRouteValues { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(
        [FromRoute] int id, [FromQuery] string? pageNumber, CancellationToken cancellationToken)
    {
        var listing = await catalogService.GetDetailsAsync(id, cancellationToken);
        if (listing is null)
        {
            return NotFound();
        }

        Listing = listing;
        // Rebuild a catalog link from validated selections, never from a submitted return URL.
        if (ModelState.IsValid)
        {
            CatalogRouteValues = CatalogNavigation.RouteValues(Filter, CatalogNavigation.ParsePageNumber(pageNumber));
        }
        return Page();
    }
}
