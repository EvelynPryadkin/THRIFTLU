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

public class EditModel(ListingService listingService, ILogger<EditModel> logger) : PageModel
{
    [BindProperty]
    public UpdateListingInput Input { get; set; } = new();

    public int ListingId { get; private set; }
    public bool CanEdit { get; private set; }
    public string? UnavailableMessage { get; private set; }
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync([FromQuery] int id, CancellationToken cancellationToken)
    {
        ListingId = id;
        var result = await listingService.GetForEditAsync(id, cancellationToken);
        if (result.Status == ListingResultStatus.NotFound)
        {
            return NotFound();
        }

        if (!result.Succeeded)
        {
            return NotEditable();
        }

        Input = result.Value!;
        CanEdit = true;
        return await RedisplayAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync([FromQuery] int id, CancellationToken cancellationToken)
    {
        ListingId = id;
        // Check the requested listing even when the posted fields are invalid, without overwriting those fields.
        var current = await listingService.GetForEditAsync(id, cancellationToken);
        if (current.Status == ListingResultStatus.NotFound)
        {
            return NotFound();
        }

        if (!current.Succeeded)
        {
            return NotEditable();
        }

        CanEdit = true;
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
            result = await listingService.UpdateAsync(id, Input, actorUserId, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError("Updating an inventory listing failed during persistence.");
            ModelState.AddModelError(string.Empty, "We couldn't save the listing. Please try again.");
            return await RedisplayAsync(cancellationToken);
        }

        if (result.Succeeded)
        {
            TempData[nameof(IndexModel.InventoryMessage)] = "Listing updated.";
            return RedirectToPage("./Index");
        }

        if (result.Status == ListingResultStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Status == ListingResultStatus.NotEditable)
        {
            return NotEditable();
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

    private PageResult NotEditable()
    {
        CanEdit = false;
        UnavailableMessage = "This listing cannot be edited in its current status. Return to inventory to check its status.";
        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }

    private async Task<PageResult> RedisplayAsync(CancellationToken cancellationToken)
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

        return Page();
    }
}
