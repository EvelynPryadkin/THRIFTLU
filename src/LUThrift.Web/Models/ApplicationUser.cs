using Microsoft.AspNetCore.Identity;

namespace LUThrift.Web.Models;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Reservation> Reservations { get; set; } = [];
}
