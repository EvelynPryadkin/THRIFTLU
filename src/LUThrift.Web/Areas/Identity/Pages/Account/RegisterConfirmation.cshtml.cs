using LUThrift.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Areas.Identity.Pages.Account;

[AllowAnonymous, ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class RegisterConfirmationModel : PageModel
{
    [TempData] public bool? ConfirmationDeliveryFailed { get; set; }
    public string ReturnUrl { get; private set; } = "/";
    public void OnGet(string? returnUrl = null) => ReturnUrl = EmailConfirmationService.SafeReturnUrl(returnUrl);
}
