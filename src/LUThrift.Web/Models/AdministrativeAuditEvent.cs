namespace LUThrift.Web.Models;

public class AdministrativeAuditEvent
{
    public int Id { get; set; }

    // Historical identifiers, not relationships to mutable account records.
    public string ActorUserId { get; set; } = string.Empty;
    public string TargetUserId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}
