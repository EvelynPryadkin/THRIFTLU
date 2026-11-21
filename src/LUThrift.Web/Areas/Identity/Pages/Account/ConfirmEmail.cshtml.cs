using LUThrift.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Areas.Identity.Pages.Account;

[AllowAnonymous, ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class ConfirmEmailModel(EmailConfirmationService confirmations) : PageModel
{
    [TempData] public bool? EmailConfirmationSucceeded { get; set; }
    public string ReturnUrl { get; private set; } = "/";
    public async Task<IActionResult> OnGetAsync(string? userId, string? code, string? returnUrl, CancellationToken cancellationToken)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        ReturnUrl = EmailConfirmationService.SafeReturnUrl(returnUrl);
        if (userId is not null || code is not null)
        {
            EmailConfirmationSucceeded = await confirmations.ConfirmAsync(userId, code, cancellationToken);
            // Strip bearer-token parameters before rendering any page or navigation.
            return RedirectToPage("./ConfirmEmail", new { returnUrl = ReturnUrl });
        }
        return Page();
    }
}
