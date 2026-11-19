using System.Globalization;
using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Admin.Users;

[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class IndexModel(StaffAccessService staffAccess) : PageModel
{
    public StaffAccessQueryInput Filter { get; private set; } = new();
    public StaffAccessPage Accounts { get; private set; } = new([], 0, 1);
    public bool HasSearch => Filter.Search is not null;

    [TempData]
    public string? StaffAccessMessage { get; set; }

    [TempData]
    public bool StaffAccessChanged { get; set; }

    public Dictionary<string, string> RouteValues(int pageNumber)
    {
        var values = new Dictionary<string, string>
        {
            ["pageNumber"] = pageNumber.ToString(CultureInfo.InvariantCulture)
        };
        if (Filter.Search is { } search) values["Filter.Search"] = search;
        return values;
    }

    public async Task<IActionResult> OnGetAsync([FromQuery(Name = "Filter")] StaffAccessQueryInput filter,
        [FromQuery] string? pageNumber, CancellationToken cancellationToken)
    {
        Filter = filter;
        var requestedPage = int.TryParse(pageNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 1;
        // Even invalid searches pass through the service's current-account gate.
        var result = await staffAccess.GetPageAsync(ModelState.IsValid ? Filter : null,
            User, requestedPage, cancellationToken);
        if (result.Status == StaffAccessQueryStatus.AuthenticationRequired) return Challenge();
        if (result.Status == StaffAccessQueryStatus.AdministratorRequired) return Forbid();

        if (ModelState.ContainsKey("Filter.Search"))
            ModelState.SetModelValue("Filter.Search", Filter.Search, Filter.Search);
        if (result.Status == StaffAccessQueryStatus.InvalidInput)
        {
            if (ModelState.IsValid) ModelState.AddModelError(string.Empty, result.Message);
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }
        Accounts = result.Page!;
        return Page();
    }

    public async Task<IActionResult> OnPostChangeAsync([FromForm] string? targetUserId,
        [FromForm] StaffAccessAction? action, CancellationToken cancellationToken)
    {
        // Only a target ID and a controlled action are accepted. The actor comes
        // from server authentication, and the service rechecks current membership.
        var requestedAction = ModelState.IsValid && action.HasValue ? action.Value : (StaffAccessAction)0;
        var result = await staffAccess.ChangeStaffAsync(targetUserId, requestedAction, User, cancellationToken);
        if (result.Status == StaffAccessOperationStatus.AuthenticationRequired) return Challenge();
        if (result.Status == StaffAccessOperationStatus.AdministratorRequired) return Forbid();
        if (result.Status == StaffAccessOperationStatus.InvalidInput) return BadRequest(result.Message);

        StaffAccessMessage = result.Message;
        StaffAccessChanged = result.Status == StaffAccessOperationStatus.Changed;
        // Only the service's committed outcome can produce a success message.
        // Refreshing the redirected page never repeats the membership change.
        return RedirectToPage("./Index");
    }
}
