using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages;

[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class StatusModel : PageModel
{
    public int ResponseStatusCode { get; private set; }
    public string RequestId { get; private set; } = string.Empty;

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        // Only the route determines the response code. Query/form values and the
        // original failed URL are neither bound nor displayed.
        var routeCode = Convert.ToString(RouteData.Values["statusCode"], CultureInfo.InvariantCulture);
        ResponseStatusCode = int.TryParse(routeCode, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            && status is >= 400 and <= 599 ? status : StatusCodes.Status404NotFound;
        Response.StatusCode = ResponseStatusCode;
        RequestId = HttpContext.TraceIdentifier;
    }
}
