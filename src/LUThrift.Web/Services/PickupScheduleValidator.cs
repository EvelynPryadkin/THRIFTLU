using System.Globalization;
using LUThrift.Web.Configuration;

namespace LUThrift.Web.Services;

public sealed record ValidatedPickupInterval(DayOfWeek Day, TimeOnly OpensAt, TimeOnly ClosesAt);

public sealed record PickupScheduleValidationResult(
    TimeZoneInfo? TimeZone,
    IReadOnlyList<ValidatedPickupInterval> Intervals,
    IReadOnlyList<DateOnly> ClosedDates,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public bool IsConfigured => IsValid && Intervals.Count > 0;
}

public static class PickupScheduleValidator
{
    public const int MaximumWeeklyIntervals = 42;
    public const int MaximumClosedDates = 366;
    public const int MaximumTimeZoneLength = 100;

    public static PickupScheduleValidationResult Validate(ReservationOptions settings)
    {
        var errors = new List<string>();
        var intervals = new List<ValidatedPickupInterval>();
        var closedDates = new HashSet<DateOnly>();
        TimeZoneInfo? timeZone = null;

        var timeZoneId = settings.CampusTimeZone?.Trim();
        if (string.IsNullOrEmpty(timeZoneId) || timeZoneId.Length > MaximumTimeZoneLength)
        {
            errors.Add("Choose a valid campus time zone of at most 100 characters.");
        }
        else
        {
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                errors.Add("Choose a recognized campus time zone.");
            }
        }

        if (settings.WeeklyPickupIntervals is null || settings.WeeklyPickupIntervals.Count > MaximumWeeklyIntervals)
        {
            errors.Add("Provide a weekly schedule with at most 42 opening intervals.");
        }
        else
        {
            foreach (var interval in settings.WeeklyPickupIntervals)
            {
                var dayText = interval?.Day?.Trim();
                var opensText = interval?.OpensAt?.Trim();
                var closesText = interval?.ClosesAt?.Trim();
                if (dayText is null || dayText.Length > 9
                    || !Enum.GetNames<DayOfWeek>().Contains(dayText, StringComparer.OrdinalIgnoreCase)
                    || !Enum.TryParse<DayOfWeek>(dayText, true, out var day)
                    || opensText?.Length != 5 || closesText?.Length != 5
                    || !TimeOnly.TryParseExact(opensText, "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var opensAt)
                    || !TimeOnly.TryParseExact(closesText, "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var closesAt)
                    || opensAt >= closesAt)
                {
                    errors.Add("Each opening interval needs a weekday name and valid HH:mm times, with closing after opening on the same day.");
                    break;
                }

                intervals.Add(new(day, opensAt, closesAt));
            }
        }

        var ordered = intervals.OrderBy(interval => interval.Day).ThenBy(interval => interval.OpensAt).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            // Continuous opening hours belong in one interval. Separate intervals need a real break.
            if (ordered[index].Day == ordered[index - 1].Day
                && ordered[index].OpensAt <= ordered[index - 1].ClosesAt)
            {
                errors.Add("Opening intervals on the same day must not overlap or touch. Combine continuous hours into one interval.");
                break;
            }
        }

        if (settings.ClosedDates is null || settings.ClosedDates.Count > MaximumClosedDates)
        {
            errors.Add("Provide a closed-date list with at most 366 dates.");
        }
        else
        {
            foreach (var date in settings.ClosedDates)
            {
                var dateText = date?.Trim();
                if (dateText?.Length != 10
                    || !DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var closedDate))
                {
                    errors.Add("Closed dates must be valid calendar dates in yyyy-MM-dd format.");
                    break;
                }

                closedDates.Add(closedDate);
            }
        }

        // Never expose a partially parsed schedule as usable output.
        return errors.Count == 0
            ? new(timeZone, ordered, closedDates.Order().ToArray(), errors.ToArray())
            : new(timeZone, [], [], errors.ToArray());
    }
}
