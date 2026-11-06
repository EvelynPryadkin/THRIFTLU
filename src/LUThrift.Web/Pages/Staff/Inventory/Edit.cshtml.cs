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

[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
[RequestSizeLimit(6 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
public class EditModel(ListingService listingService, ImageService imageService, ILogger<EditModel> logger) : PageModel
{
    public UpdateListingInput Input { get; private set; } = new();

    public int ListingId { get; private set; }
    public bool CanEdit { get; private set; }
    public string? UnavailableMessage { get; private set; }
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];
    public Guid? ImageId { get; private set; }
    public string? PhotoError { get; private set; }

    [TempData]
    public string? PhotoMessage { get; set; }

    public async Task<IActionResult> OnGetAsync([FromQuery] int id, CancellationToken cancellationToken)
    {
        var unavailable = await LoadCurrentListingAsync(id, copyInput: true, cancellationToken);
        if (unavailable is not null) return unavailable;
        return await RedisplayAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync([FromQuery] int id,
        [FromForm(Name = nameof(Input))] UpdateListingInput input, CancellationToken cancellationToken)
    {
        // Bind listing fields only for this handler. Photo forms have their own
        // inputs and must not validate an empty UpdateListingInput.
        Input = input;
        // Check the requested listing even when the posted fields are invalid, without overwriting those fields.
        var unavailable = await LoadCurrentListingAsync(id, copyInput: false, cancellationToken);
        if (unavailable is not null) return unavailable;
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

    public async Task<IActionResult> OnPostUploadPhotoAsync(
        [FromQuery] int id, [FromForm] IFormFile? photo, [FromForm] Guid? expectedImageId,
        CancellationToken cancellationToken)
    {
        var unavailable = await LoadCurrentListingAsync(id, copyInput: true, cancellationToken);
        if (unavailable is not null) return unavailable;
        if (!ModelState.IsValid)
        {
            PhotoError = "The photo form is invalid. Refresh this page and choose the photo again.";
            return await RedisplayAsync(cancellationToken);
        }
        if (photo is null || photo.Length == 0)
        {
            PhotoError = "Choose a JPEG or PNG photo to upload.";
            return await RedisplayAsync(cancellationToken);
        }
        if (photo.Length > ListingImageProcessor.MaximumUploadBytes)
        {
            PhotoError = "Choose a photo no larger than 5 MiB.";
            return await RedisplayAsync(cancellationToken);
        }

        // Filename, content type, identities and timestamps supplied by the browser
        // do not authorize or describe the stored image. The service decodes bytes.
        await using var stream = photo.OpenReadStream();
        var result = await imageService.AddOrReplaceAsync(id, stream, User, expectedImageId, cancellationToken);
        return await PhotoOutcomeAsync(id, result, cancellationToken);
    }

    public async Task<IActionResult> OnPostRemovePhotoAsync(
        [FromQuery] int id, [FromForm] Guid? expectedImageId, CancellationToken cancellationToken)
    {
        var unavailable = await LoadCurrentListingAsync(id, copyInput: true, cancellationToken);
        if (unavailable is not null) return unavailable;
        if (!ModelState.IsValid || expectedImageId is null || expectedImageId == Guid.Empty)
        {
            PhotoError = "Refresh this page before removing the photo.";
            return await RedisplayAsync(cancellationToken);
        }
        var result = await imageService.RemoveAsync(id, expectedImageId.Value, User, cancellationToken);
        return await PhotoOutcomeAsync(id, result, cancellationToken);
    }

    private async Task<IActionResult> PhotoOutcomeAsync(
        int id, ImageOperationResult result, CancellationToken cancellationToken)
    {
        var denied = ImageAccessResult(result);
        if (denied is not null) return denied;
        if (result.Succeeded)
        {
            PhotoMessage = result.Message + (result.CleanupPending
                ? " An older photo could not be cleaned up; please notify the administrator." : string.Empty);
            return RedirectToPage("./Edit", new { id });
        }

        PhotoError = result.Message;
        if (result.Status == ImageOperationStatus.Conflict) Response.StatusCode = StatusCodes.Status409Conflict;
        // Reload the current photo ID after a stale form or a failed save. This
        // preserves the previous preview and keeps retry forms tied to current state.
        var unavailable = await LoadCurrentListingAsync(id, copyInput: true, cancellationToken);
        if (unavailable is not null) return unavailable;
        return await RedisplayAsync(cancellationToken);
    }

    private async Task<IActionResult?> LoadCurrentListingAsync(
        int id, bool copyInput, CancellationToken cancellationToken)
    {
        ListingId = id;
        var image = await imageService.GetForEditAsync(id, User, cancellationToken);
        var denied = ImageAccessResult(image);
        if (denied is not null) return denied;
        if (!image.Succeeded)
        {
            PhotoError = image.Message;
            return NotEditable();
        }
        ImageId = image.ImageId;

        var listing = await listingService.GetForEditAsync(id, cancellationToken);
        if (listing.Status == ListingResultStatus.NotFound) return NotFound();
        if (!listing.Succeeded) return NotEditable();
        if (copyInput) Input = listing.Value!;
        CanEdit = true;
        return null;
    }

    private IActionResult? ImageAccessResult(ImageOperationResult result) => result.Status switch
    {
        ImageOperationStatus.AuthenticationRequired => Challenge(),
        ImageOperationStatus.StaffRequired => Forbid(),
        ImageOperationStatus.NotFound => NotFound(),
        ImageOperationStatus.NotEditable => NotEditable(),
        _ => null
    };

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
