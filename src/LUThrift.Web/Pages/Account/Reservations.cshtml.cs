using LUThrift.Web.Authorization;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Account;

[Authorize(Roles = ApplicationRoles.Student)]
public class ReservationsModel(ReservationService reservations) : PageModel
{
    public IReadOnlyList<ReservationSummary> Reservations { get; private set; } = [];

    [TempData]
    public string? ReservationMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
        => Reservations = await reservations.GetForUserAsync(User, cancellationToken);

    public async Task<IActionResult> OnPostCancelAsync([FromForm] int id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var result = await reservations.CancelAsync(id, User, cancellationToken);
        if (result.Status == ReservationOperationStatus.AuthenticationRequired)
        {
            return Challenge();
        }
        if (result.Status == ReservationOperationStatus.StudentRequired)
        {
            return Forbid();
        }

        ReservationMessage = result.Message;
        return RedirectToPage();
    }
}
