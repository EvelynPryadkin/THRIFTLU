namespace LUThrift.Web.Models;

public class Reservation
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Active;
    public DateTime? FulfilledAtUtc { get; set; }
    public string? CancellationReason { get; set; }
}
