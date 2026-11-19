namespace LUThrift.Web.Models;

public sealed class PickupSchedule
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public Guid Version { get; set; }
    public string CampusTimeZone { get; set; } = string.Empty;
    public ICollection<PickupScheduleInterval> WeeklyIntervals { get; set; } = [];
    public ICollection<PickupScheduleClosedDate> ClosedDates { get; set; } = [];
}

public sealed class PickupScheduleInterval
{
    public int ScheduleId { get; set; }
    public PickupSchedule Schedule { get; set; } = null!;
    public DayOfWeek Day { get; set; }
    public TimeOnly OpensAt { get; set; }
    public TimeOnly ClosesAt { get; set; }
}

public sealed class PickupScheduleClosedDate
{
    public int ScheduleId { get; set; }
    public PickupSchedule Schedule { get; set; } = null!;
    public DateOnly Date { get; set; }
}
