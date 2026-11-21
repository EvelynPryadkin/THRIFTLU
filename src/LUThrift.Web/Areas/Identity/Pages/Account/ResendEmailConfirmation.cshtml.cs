using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Areas.Identity.Pages.Account;

[AllowAnonymous, ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class ResendEmailConfirmationModel(EmailConfirmationService confirmations) : PageModel
{
    [BindProperty, Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;
    [TempData] public bool ConfirmationRequested { get; set; }
    public string ReturnUrl { get; private set; } = "/";

    public void OnGet(string? returnUrl = null) => ReturnUrl = EmailConfirmationService.SafeReturnUrl(returnUrl);
    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        ReturnUrl = EmailConfirmationService.SafeReturnUrl(returnUrl);
        if (!ModelState.IsValid) return Page();
        await confirmations.ResendAsync(Email, ReturnUrl, cancellationToken);
        ConfirmationRequested = true;
        return RedirectToPage("./ResendEmailConfirmation", new { returnUrl = ReturnUrl });
    }
}
