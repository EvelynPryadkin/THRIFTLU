using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class RegisterModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ApplicationDbContext dbContext,
    ILogger<RegisterModel> logger) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string ReturnUrl { get; private set; } = "/";

    public class InputModel
    {
        [Required]
        [StringLength(100)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [StringLength(256)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MinLength(8)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
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

        var email = Input.Email.Trim();
        var user = new ApplicationUser
        {
            DisplayName = Input.DisplayName.Trim(),
            UserName = email,
            Email = email,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true
        };

        // User creation and the Student role must succeed together before signing in.
        await using (var transaction = await dbContext.Database.BeginTransactionAsync())
        {
            var result = await userManager.CreateAsync(user, Input.Password);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return Page();
            }

            IdentityResult assignment;
            try
            {
                assignment = await userManager.AddToRoleAsync(user, ApplicationRoles.Student);
            }
            catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
            {
                return RoleAssignmentFailed();
            }

            if (!assignment.Succeeded)
            {
                return RoleAssignmentFailed();
            }

            await transaction.CommitAsync();
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect(ReturnUrl);
    }

    private PageResult RoleAssignmentFailed()
    {
        // Keep account details and store error descriptions out of the response and logs.
        logger.LogError("Student role assignment failed; registration was not completed.");
        ModelState.AddModelError(string.Empty, "We couldn't finish creating your account. Please try again.");
        return Page();
    }
}
