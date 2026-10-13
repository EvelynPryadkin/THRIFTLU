using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Thrift;

public class IndexModel(CatalogService catalogService) : PageModel
{
    public IReadOnlyList<CatalogListingSummary> Listings { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Listings = await catalogService.GetListingsAsync(cancellationToken);
    }
}
