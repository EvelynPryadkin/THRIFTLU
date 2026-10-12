using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Security.Claims;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Pages.Staff.Inventory;

public class IndexModel(ListingService listingService, ILogger<IndexModel> logger) : PageModel
{
    [TempData]
    public string? InventoryMessage { get; set; }

    [TempData]
    public string? InventoryError { get; set; }

    // This is a display filter only; lifecycle methods never use it as a new status.
    [BindProperty(SupportsGet = true)]
    [FromQuery]
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

    public Task<IActionResult> OnPostPublishAsync([FromQuery] int id, CancellationToken cancellationToken)
        => ApplyActionAsync(id, listingService.PublishAsync, "Listing published.", cancellationToken);

    public Task<IActionResult> OnPostArchiveAsync([FromQuery] int id, CancellationToken cancellationToken)
        => ApplyActionAsync(id, listingService.ArchiveAsync, "Listing archived.", cancellationToken);

    public Task<IActionResult> OnPostMarkMissingAsync([FromQuery] int id, CancellationToken cancellationToken)
        => ApplyActionAsync(id, listingService.MarkMissingAsync, "Listing marked as missing.", cancellationToken);

    public Task<IActionResult> OnPostRecordWalkInCollectionAsync([FromQuery] int id, CancellationToken cancellationToken)
        => ApplyActionAsync(id, listingService.RecordWalkInCollectionAsync, "Walk-in collection recorded.", cancellationToken);

    private async Task<IActionResult> ApplyActionAsync(
        int listingId, Func<int, string?, CancellationToken, Task<ListingResult<int>>> action,
        string successMessage, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest("The inventory request is invalid. Refresh the inventory and try again.");
        }

        var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorUserId))
        {
            return Forbid();
        }

        // Clear an earlier unconsumed message before reporting this request's outcome.
        InventoryMessage = null;
        InventoryError = null;
        ListingResult<int> result;
        try
        {
            result = await action(listingId, actorUserId, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError("An inventory lifecycle action failed during persistence.");
            InventoryError = "We couldn't save the inventory change. Please try again.";
            return RedirectToPage("./Index");
        }

        if (result.Status == ListingResultStatus.InvalidActor)
        {
            return Forbid();
        }

        if (result.Succeeded)
        {
            InventoryMessage = successMessage;
        }
        else
        {
            InventoryError = result.Errors.FirstOrDefault()?.ErrorMessage ?? "The inventory change could not be completed.";
        }

        return RedirectToPage("./Index");
    }
}
