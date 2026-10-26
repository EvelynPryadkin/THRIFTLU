using System.Globalization;
using LUThrift.Web.Configuration;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Services;

public enum PickupHoursStatus
{
    Eligible,
    ScheduleUnavailable,
    Closed,
    InsufficientTime
}

public sealed record PickupHoursResult(
    PickupHoursStatus Status,
    DateTimeOffset? StartsAtUtc = null,
    DateTimeOffset? ExpiresAtUtc = null)
{
    public bool IsEligible => Status == PickupHoursStatus.Eligible;
}

public sealed class PickupHoursEligibility
{
    private readonly TimeProvider timeProvider;
    private readonly TimeZoneInfo? campusTimeZone;
    private readonly List<OpenInterval> intervals = [];
    private readonly HashSet<DateOnly> closedDates = [];
    private readonly bool hasValidSchedule;

    public PickupHoursEligibility(IOptions<ReservationOptions> options, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.CampusTimeZone))
        {
            return;
        }

        try
        {
            campusTimeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.CampusTimeZone.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return;
        }

        hasValidSchedule = ReadSchedule(settings);
    }

    public PickupHoursResult Evaluate()
    {
        if (!hasValidSchedule || campusTimeZone is null)
        {
            return new(PickupHoursStatus.ScheduleUnavailable);
        }

        // Capture one server instant. The reservation lasts an elapsed hour,
        // including when the campus clock moves forward or backward.
        var nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
        if (nowUtc > DateTimeOffset.MaxValue - ReservationPolicy.Duration)
        {
            return new(PickupHoursStatus.ScheduleUnavailable);
        }

        var campusDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, campusTimeZone).DateTime);
        if (closedDates.Contains(campusDate))
        {
            return new(PickupHoursStatus.Closed);
        }

        var today = new List<(DateTimeOffset OpensAtUtc, DateTimeOffset ClosesAtUtc)>();
        foreach (var interval in intervals.Where(interval => interval.Day == campusDate.DayOfWeek))
        {
            var opensAt = campusDate.ToDateTime(interval.OpensAt, DateTimeKind.Unspecified);
            var closesAt = campusDate.ToDateTime(interval.ClosesAt, DateTimeKind.Unspecified);

            // Do not guess which occurrence an ambiguous endpoint means, or
            // silently move a nonexistent local time across a DST gap.
            if (campusTimeZone.IsInvalidTime(opensAt) || campusTimeZone.IsInvalidTime(closesAt)
                || campusTimeZone.IsAmbiguousTime(opensAt) || campusTimeZone.IsAmbiguousTime(closesAt))
            {
                return new(PickupHoursStatus.ScheduleUnavailable);
            }

            var opensAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(opensAt, campusTimeZone));
            var closesAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(closesAt, campusTimeZone));
            if (closesAtUtc <= opensAtUtc)
            {
                return new(PickupHoursStatus.ScheduleUnavailable);
            }

            today.Add((opensAtUtc, closesAtUtc));
        }

        var expiresAtUtc = nowUtc + ReservationPolicy.Duration;
        foreach (var interval in today)
        {
            if (nowUtc >= interval.OpensAtUtc && nowUtc < interval.ClosesAtUtc)
            {
                return expiresAtUtc <= interval.ClosesAtUtc
                    ? new(PickupHoursStatus.Eligible, nowUtc, expiresAtUtc)
                    : new(PickupHoursStatus.InsufficientTime);
            }
        }

        return new(PickupHoursStatus.Closed);
    }

    private bool ReadSchedule(ReservationOptions settings)
    {
        if (settings.WeeklyPickupIntervals is null || settings.WeeklyPickupIntervals.Count == 0
            || settings.ClosedDates is null)
        {
            return false;
        }

        foreach (var date in settings.ClosedDates)
        {
            if (!DateOnly.TryParseExact(date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var closedDate))
            {
                return false;
            }

            closedDates.Add(closedDate);
        }

        foreach (var interval in settings.WeeklyPickupIntervals)
        {
            if (interval is null
                || !Enum.GetNames<DayOfWeek>().Contains(interval.Day?.Trim(), StringComparer.OrdinalIgnoreCase)
                || !Enum.TryParse<DayOfWeek>(interval.Day?.Trim(), true, out var day)
                || !TimeOnly.TryParseExact(interval.OpensAt?.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var opensAt)
                || !TimeOnly.TryParseExact(interval.ClosesAt?.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var closesAt)
                || opensAt >= closesAt)
            {
                return false;
            }

            intervals.Add(new(day, opensAt, closesAt));
        }

        foreach (var day in intervals.GroupBy(interval => interval.Day))
        {
            var ordered = day.OrderBy(interval => interval.OpensAt).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                // A continuous span must be configured as one interval.
                // Separate intervals must have a real break between them.
                if (ordered[index].OpensAt <= ordered[index - 1].ClosesAt)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private sealed record OpenInterval(DayOfWeek Day, TimeOnly OpensAt, TimeOnly ClosesAt);
}
