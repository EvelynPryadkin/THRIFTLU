namespace LUThrift.Web.Configuration;

public sealed class ReservationOptions
{
    public const string SectionName = "Reservations";

    public string CampusTimeZone { get; set; } = "America/Chicago";
    public List<WeeklyPickupInterval> WeeklyPickupIntervals { get; set; } = [];
    public List<string> ClosedDates { get; set; } = [];
}

public sealed class WeeklyPickupInterval
{
    // Parse schedule text in the eligibility helper so bad settings disable
    // reservations instead of throwing during configuration binding.
    public string Day { get; set; } = string.Empty;
    public string OpensAt { get; set; } = string.Empty;
    public string ClosesAt { get; set; } = string.Empty;
}
