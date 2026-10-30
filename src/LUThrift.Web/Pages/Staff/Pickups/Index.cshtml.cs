using System.Globalization;
using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

namespace LUThrift.Web.Pages.Staff.Pickups;

[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class IndexModel(StaffPickupService pickups, ReservationService reservations) : PageModel
{
    public StaffPickupQueryInput Filter { get; private set; } = new();

    [TempData]
    public string? PickupMessage { get; set; }

    public StaffPickupPage Pickups { get; private set; } = new([], 0, 1);
    public bool HasFilters => Filter.ReservationId.HasValue || Filter.StudentEmail is not null;

    public Dictionary<string, string> RouteValues(int pageNumber)
    {
        var values = new Dictionary<string, string>
        {
            ["pageNumber"] = pageNumber.ToString(CultureInfo.InvariantCulture)
        };
        if (Filter.ReservationId is { } id)
            values["Filter.ReservationId"] = id.ToString(CultureInfo.InvariantCulture);
        if (Filter.StudentEmail is { } email)
            values["Filter.StudentEmail"] = email;
        return values;
    }

    public async Task<IActionResult> OnGetAsync([FromQuery(Name = "Filter")] StaffPickupQueryInput filter,
        [FromQuery] string? pageNumber, CancellationToken cancellationToken)
    {
        Filter = filter;
        var requestedPage = int.TryParse(pageNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 1;

        // Never turn a malformed ID into an unfiltered query. Still use the service
        // to enforce current-account access before displaying validation feedback.
        var result = await pickups.GetPageAsync(ModelState.IsValid ? Filter : null, User,
            requestedPage, cancellationToken);
        if (result.Status == StaffPickupQueryStatus.AuthenticationRequired) return Challenge();
        if (result.Status == StaffPickupQueryStatus.StaffRequired) return Forbid();

        // Tag Helpers otherwise redisplay the untrimmed query value.
        if (ModelState.ContainsKey("Filter.StudentEmail"))
            ModelState.SetModelValue("Filter.StudentEmail", Filter.StudentEmail, Filter.StudentEmail);

        if (result.Status == StaffPickupQueryStatus.InvalidInput)
        {
            if (ModelState.IsValid) ModelState.AddModelError(string.Empty, result.Message);
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }

        Pickups = result.Page!;
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmPickupAsync([FromForm] int reservationId,
        CancellationToken cancellationToken)
    {
        // Only the form's reservation ID is bound. Query IDs and submitted identities,
        // statuses, timestamps, and search fields never choose or authorize a transition.
        if (!ModelState.IsValid) reservationId = 0;
        var result = await reservations.ConfirmPickupAsync(reservationId, User, cancellationToken);
        if (result.Status == ReservationOperationStatus.AuthenticationRequired) return Challenge();
        if (result.Status == ReservationOperationStatus.StaffRequired) return Forbid();

        PickupMessage = result.Status == ReservationOperationStatus.NotActive
            ? "This reservation is already completed or is no longer active. No new pickup was recorded."
            : result.Message;

        // ConfirmPickupAsync returns Fulfilled only after committing. PRG makes a
        // browser refresh a read and leaves the selected record available for review.
        return reservationId > 0
            ? RedirectToPage("./Index", new RouteValueDictionary { ["Filter.ReservationId"] = reservationId })
            : RedirectToPage("./Index");
    }

    public async Task<IActionResult> OnPostReportMissingAsync([FromForm] int reservationId,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) reservationId = 0;
        var result = await reservations.ReportMissingAsync(reservationId, User, cancellationToken);
        if (result.Status == ReservationOperationStatus.AuthenticationRequired) return Challenge();
        if (result.Status == ReservationOperationStatus.StaffRequired) return Forbid();

        PickupMessage = result.Message;
        return reservationId > 0
            ? RedirectToPage("./Index", new RouteValueDictionary { ["Filter.ReservationId"] = reservationId })
            : RedirectToPage("./Index");
    }
}
