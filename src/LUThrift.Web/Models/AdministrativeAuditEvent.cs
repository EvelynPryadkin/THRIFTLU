namespace LUThrift.Web.Models;

public class AdministrativeAuditEvent
{
    public int Id { get; set; }

    // Historical identifiers, not relationships to mutable accounts or schedules.
    public string ActorUserId { get; set; } = string.Empty;
    public string? TargetUserId { get; set; }
    public int? TargetScheduleId { get; set; }
    public string Action { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}
