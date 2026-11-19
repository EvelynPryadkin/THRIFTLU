using LUThrift.Web.Configuration;

namespace LUThrift.Web.InputModels;

// Schedule fields only: reservation duration/limits and audit identity are never submitted.
public sealed class UpdatePickupScheduleInput
{
    public Guid? ExpectedVersion { get; set; }
    public string CampusTimeZone { get; set; } = "America/Chicago";
    public List<WeeklyPickupInterval> WeeklyPickupIntervals { get; set; } = [];
    public List<string> ClosedDates { get; set; } = [];
}
