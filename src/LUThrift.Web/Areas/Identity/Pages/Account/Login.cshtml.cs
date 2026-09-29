using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class LoginModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string ReturnUrl { get; private set; } = string.Empty;

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Content("~/");

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await userManager.FindByEmailAsync(Input.Email.Trim());
        if (user is not null)
        {
            var result = await signInManager.PasswordSignInAsync(
                user, Input.Password, Input.RememberMe, lockoutOnFailure: true);

            if (result.Succeeded)
            {
                return LocalRedirect(ReturnUrl);
            }
        }

        ModelState.AddModelError(string.Empty, "Invalid email or password.");
        return Page();
    }
}
