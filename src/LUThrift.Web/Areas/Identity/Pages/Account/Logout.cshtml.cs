using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class LogoutModel(SignInManager<ApplicationUser> signInManager) : PageModel
{
    public string ReturnUrl { get; private set; } = string.Empty;

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        await signInManager.SignOutAsync();
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Content("~/"));
    }
}
