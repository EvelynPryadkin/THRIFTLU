using System.Globalization;
using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Admin.Activity;

// The folder policy and service both require current active Administrator membership.
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class IndexModel(ActivityHistoryService activity) : PageModel
{
    public ActivityHistoryQueryInput Filter { get; private set; } = new();
    public ActivityHistoryPage History { get; private set; } = new([], 0, 1);
    public bool IsAdministration => Filter.History == ActivityHistoryKind.Administration;
    public string HistoryLabel => IsAdministration ? "Administrative activity" : "Listing, reservation, and photo activity";
    public IReadOnlyDictionary<string, string> ActionOptions => ActivityHistoryPresentation.Actions(
        IsAdministration ? ActivityHistoryKind.Administration : ActivityHistoryKind.Inventory);

    public Dictionary<string, string> RouteValues(int pageNumber)
    {
        var values = new Dictionary<string, string>
        {
            ["Filter.History"] = Filter.History.ToString(),
            ["pageNumber"] = pageNumber.ToString(CultureInfo.InvariantCulture)
        };
        if (Filter.Action is { } action) values["Filter.Action"] = action;
        if (Filter.ActorId is { } actor) values["Filter.ActorId"] = actor;
        if (Filter.ListingId is { } listing) values["Filter.ListingId"] = listing.ToString(CultureInfo.InvariantCulture);
        if (Filter.TargetUserId is { } target) values["Filter.TargetUserId"] = target;
        if (Filter.ScheduleId is { } schedule) values["Filter.ScheduleId"] = schedule.ToString(CultureInfo.InvariantCulture);
        return values;
    }

    public async Task<IActionResult> OnGetAsync([FromQuery(Name = "Filter")] ActivityHistoryQueryInput filter,
        [FromQuery] string? pageNumber, CancellationToken cancellationToken)
    {
        Filter = filter;
        var requestedPage = int.TryParse(pageNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 1;
        // Invalid filters still pass through the service's current-account gate.
        var result = await activity.GetPageAsync(ModelState.IsValid ? Filter : null, User, requestedPage, cancellationToken);
        if (result.Status == ActivityHistoryQueryStatus.AuthenticationRequired) return Challenge();
        if (result.Status == ActivityHistoryQueryStatus.AdministratorRequired) return Forbid();

        foreach (var (name, value) in new[]
                 { ("Filter.Action", Filter.Action), ("Filter.ActorId", Filter.ActorId), ("Filter.TargetUserId", Filter.TargetUserId) })
            if (ModelState.ContainsKey(name)) ModelState.SetModelValue(name, value, value);
        if (result.Status == ActivityHistoryQueryStatus.InvalidInput)
        {
            if (ModelState.IsValid) ModelState.AddModelError(string.Empty, result.Message);
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }
        History = result.Page!;
        return Page();
    }
}
