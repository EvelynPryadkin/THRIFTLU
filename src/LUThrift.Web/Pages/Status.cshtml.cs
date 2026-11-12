using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace LUThrift.Web.Pages;

[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class StatusModel : PageModel
{
    public int ResponseStatusCode { get; private set; }
    public string RequestId { get; private set; } = string.Empty;
    public bool PhotoUploadRejected { get; private set; }
    public int? PhotoListingId { get; private set; }

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        // Only the route determines the response code. Never display the failed URL
        // or read a rejected request body again.
        var routeCode = Convert.ToString(RouteData.Values["statusCode"], CultureInfo.InvariantCulture);
        ResponseStatusCode = int.TryParse(routeCode, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            && status is >= 400 and <= 599 ? status : StatusCodes.Status404NotFound;
        Response.StatusCode = ResponseStatusCode;
        RequestId = HttpContext.TraceIdentifier;

        var failedRequest = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        if (ResponseStatusCode is not (StatusCodes.Status400BadRequest or StatusCodes.Status413PayloadTooLarge)
            || !HttpMethods.IsPost(Request.Method)
            || failedRequest is null
            || !string.Equals(failedRequest.OriginalPath.TrimEnd('/'), "/Staff/Inventory/Edit", StringComparison.OrdinalIgnoreCase))
            return;

        // Multipart/request limits and antiforgery can reject an upload before its
        // handler runs. Give safe size/refresh guidance without assuming the cause.
        var query = QueryHelpers.ParseQuery(failedRequest.OriginalQueryString);
        if (!query.TryGetValue("handler", out var handlers)
            || !string.Equals(handlers.FirstOrDefault(), "UploadPhoto", StringComparison.OrdinalIgnoreCase))
            return;

        PhotoUploadRejected = true;
        var listingId = query.TryGetValue("id", out var ids) ? ids.FirstOrDefault() : null;
        if (listingId is { Length: <= 10 }
            && int.TryParse(listingId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            PhotoListingId = id;
    }
}
