using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Pages.Staff.Inventory;

public class CreateModel(ListingService listingService, ILogger<CreateModel> logger) : PageModel
{
    [BindProperty]
    public CreateListingInput Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
        => await LoadCategoriesAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return await RedisplayAsync(cancellationToken);
        }

        var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorUserId))
        {
            return Forbid();
        }

        ListingResult<int> result;
        try
        {
            result = await listingService.CreateAsync(Input, actorUserId, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError("Creating an inventory listing failed during persistence.");
            ModelState.AddModelError(string.Empty, "We couldn't save the listing. Please try again.");
            return await RedisplayAsync(cancellationToken);
        }

        if (result.Succeeded)
        {
            TempData[nameof(IndexModel.InventoryMessage)] = "Listing created as a draft.";
            return RedirectToPage("./Index");
        }

        if (result.Status == ListingResultStatus.InvalidActor)
        {
            return Forbid();
        }

        foreach (var error in result.Errors)
        {
            foreach (var key in error.MemberNames.Select(member => $"{nameof(Input)}.{member}").DefaultIfEmpty(string.Empty))
            {
                ModelState.AddModelError(key, error.ErrorMessage ?? "Check the listing information.");
            }
        }

        return await RedisplayAsync(cancellationToken);
    }

    private async Task<PageResult> RedisplayAsync(CancellationToken cancellationToken)
    {
        await LoadCategoriesAsync(cancellationToken);
        return Page();
    }

    private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var categories = await listingService.GetCategoriesAsync(cancellationToken);
            CategoryOptions = categories.Select(category => new SelectListItem(
                category.Name, category.Id.ToString(CultureInfo.InvariantCulture))).ToArray();
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError("Inventory categories could not be loaded.");
            ModelState.AddModelError(string.Empty, "We couldn't load the categories. Please try again.");
        }
    }
}
