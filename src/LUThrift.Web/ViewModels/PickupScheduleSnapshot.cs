using LUThrift.Web.Configuration;

namespace LUThrift.Web.ViewModels;

public sealed record PickupScheduleSnapshot(
    Guid? Version,
    string CampusTimeZone,
    IReadOnlyList<WeeklyPickupInterval> WeeklyPickupIntervals,
    IReadOnlyList<string> ClosedDates)
{
    public bool IsConfigured => WeeklyPickupIntervals.Count > 0;
}
