using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages;

[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string RequestId { get; private set; } = string.Empty;

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        // Exception handling can re-execute a POST. This page only renders a
        // response, so initialize it for every method without reading the form.
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        RequestId = HttpContext.TraceIdentifier;
    }
}
