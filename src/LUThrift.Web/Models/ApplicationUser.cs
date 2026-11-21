using Microsoft.AspNetCore.Identity;

namespace LUThrift.Web.Models;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    // False for pre-existing accounts; registration sets this on the server.
    public bool EmailConfirmationRequiredForSignIn { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Reservation> Reservations { get; set; } = [];
}
