namespace LUThrift.Web.Models;

public class Listing
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Condition { get; set; } = string.Empty;
    public string? Size { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public ListingStatus Status { get; set; } = ListingStatus.Draft;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<Reservation> Reservations { get; set; } = [];
}
