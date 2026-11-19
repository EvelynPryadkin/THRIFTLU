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
    private readonly PickupScheduleService? schedules;
    private readonly TimeZoneInfo? campusTimeZone;
    private readonly IReadOnlyList<ValidatedPickupInterval> intervals = [];
    private readonly HashSet<DateOnly> closedDates = [];
    private readonly bool hasValidSchedule;

    // Production callers capture the committed database schedule for each operation.
    public PickupHoursEligibility(PickupScheduleService schedules, TimeProvider timeProvider)
    {
        this.schedules = schedules;
        this.timeProvider = timeProvider;
    }

    public PickupHoursEligibility(IOptions<ReservationOptions> options, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        var schedule = PickupScheduleValidator.Validate(options.Value);
        campusTimeZone = schedule.TimeZone;
        intervals = schedule.Intervals;
        closedDates.UnionWith(schedule.ClosedDates);
        hasValidSchedule = schedule.IsConfigured;
    }

    public async Task<PickupHoursEligibility> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (schedules is null) return this;
        var snapshot = await schedules.GetAsync(cancellationToken);
        return new PickupHoursEligibility(Options.Create(new ReservationOptions
        {
            CampusTimeZone = snapshot.CampusTimeZone,
            WeeklyPickupIntervals = snapshot.WeeklyPickupIntervals.ToList(),
            ClosedDates = snapshot.ClosedDates.ToList()
        }), timeProvider);
    }

    public async Task<PickupHoursResult> EvaluateAsync(CancellationToken cancellationToken = default)
        => (await CaptureAsync(cancellationToken)).Evaluate();

    public PickupHoursResult Evaluate()
    {
        if (schedules is not null)
            throw new InvalidOperationException("Capture the persisted pickup schedule before evaluating it.");
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
}
