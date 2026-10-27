namespace LUThrift.Web.Configuration;

public sealed class ReservationOptions
{
    public const string SectionName = "Reservations";
    public const int MaximumExpirationBatchSize = 200;
    public const int MaximumExpirationPollSeconds = 300;

    public string CampusTimeZone { get; set; } = "America/Chicago";
    public List<WeeklyPickupInterval> WeeklyPickupIntervals { get; set; } = [];
    public List<string> ClosedDates { get; set; } = [];
    public int ExpirationPollSeconds { get; set; } = 30;
    public int ExpirationBatchSize { get; set; } = 50;
}

public sealed class WeeklyPickupInterval
{
    // Parse schedule text in the eligibility helper so bad settings disable
    // reservations instead of throwing during configuration binding.
    public string Day { get; set; } = string.Empty;
    public string OpensAt { get; set; } = string.Empty;
    public string ClosesAt { get; set; } = string.Empty;
}
