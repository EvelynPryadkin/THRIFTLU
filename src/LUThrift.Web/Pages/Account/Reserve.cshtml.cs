using LUThrift.Web.Authorization;
using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Account;

// This whole page is protected, outside the anonymous /Thrift folder.
[Authorize(Roles = ApplicationRoles.Student)]
public class ReserveModel(ReservationService reservations, CampusTimeFormatter campusTime) : PageModel
{
    // Login redirects and bookmarked URLs must never create or replay a reservation.
    public IActionResult OnGet() => RedirectToPage("/Thrift/Index");

    public async Task<IActionResult> OnPostAsync(
        [FromForm] CreateReservationInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest("Choose a valid listing.");
        }

        var result = await reservations.CreateAsync(input, User, cancellationToken);
        if (result.Status == ReservationCreationStatus.AuthenticationRequired)
        {
            return Challenge();
        }
        if (result.Status == ReservationCreationStatus.StudentRequired)
        {
            return Forbid();
        }

        // Use the deadline returned after commit, never the form or another clock read.
        TempData["ReservationMessage"] = result.Succeeded
            ? $"Reservation confirmed. Collect your item by {campusTime.Format(result.ExpiresAtUtc!.Value)}."
            : result.Message;
        return RedirectToPage("/Account/Reservations");
    }
}
