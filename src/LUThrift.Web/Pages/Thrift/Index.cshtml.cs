using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Thrift;

public class IndexModel(CatalogService catalogService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public CatalogFilterInput Filter { get; set; } = new();

    public IReadOnlyList<CatalogListingSummary> Listings { get; private set; } = [];
    public IReadOnlyList<CatalogCategoryOption> Categories { get; private set; } = [];
    public IReadOnlyList<string> Sizes { get; private set; } = [];

    public bool HasFilters => Filter.Search is not null || Filter.CategoryId.HasValue
        || Filter.Size is not null || Filter.Availability != CatalogAvailability.Available;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Categories = await catalogService.GetCategoriesAsync(cancellationToken);
        Sizes = await catalogService.GetSizesAsync(cancellationToken);

        // Tag Helpers prefer ModelState values; show the normalized text without clearing validation errors.
        if (ModelState.ContainsKey("Filter.Search"))
        {
            ModelState.SetModelValue("Filter.Search", Filter.Search, Filter.Search);
        }
        if (ModelState.ContainsKey("Filter.Size"))
        {
            ModelState.SetModelValue("Filter.Size", Filter.Size, Filter.Size);
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }

        Listings = await catalogService.GetListingsAsync(Filter, cancellationToken);
        return Page();
    }
}
