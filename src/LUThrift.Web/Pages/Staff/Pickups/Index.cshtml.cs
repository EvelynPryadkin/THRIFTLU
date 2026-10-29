using System.Globalization;
using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Staff.Pickups;

[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class IndexModel(StaffPickupService pickups) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public StaffPickupQueryInput Filter { get; set; } = new();

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

    public async Task<IActionResult> OnGetAsync([FromQuery] string? pageNumber, CancellationToken cancellationToken)
    {
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
}
