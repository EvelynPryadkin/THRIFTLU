namespace LUThrift.Web.Models;

public class AuditEvent
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;

    // A historical identifier, not a relationship to the current account record.
    public string ActorUserId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string? Details { get; set; }
}
