using System.Globalization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Services;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Unit;

public class PickupHoursEligibilityTests
{
    // All intervals below are test fixtures, not LU Thrift's official pickup schedule.
    [Theory]
    [InlineData("2026-01-05T14:59:59Z", PickupHoursStatus.Closed)]
    [InlineData("2026-01-05T15:00:00Z", PickupHoursStatus.Eligible)]
    [InlineData("2026-01-05T17:00:00Z", PickupHoursStatus.Eligible)]
    [InlineData("2026-01-05T17:00:00.0000001Z", PickupHoursStatus.InsufficientTime)]
    [InlineData("2026-01-05T17:00:01Z", PickupHoursStatus.InsufficientTime)]
    [InlineData("2026-01-05T17:59:59Z", PickupHoursStatus.InsufficientTime)]
    [InlineData("2026-01-05T18:00:00Z", PickupHoursStatus.Closed)]
    [InlineData("2026-01-05T18:00:01Z", PickupHoursStatus.Closed)]
    public void OpeningAndClosingBoundaries_RequireACompleteElapsedHour(string now, PickupHoursStatus expected)
    {
        var clock = new FixedTimeProvider(Instant(now));
        var eligibility = Eligibility(Schedule(Interval("Monday", "09:00", "12:00")), clock);

        var result = eligibility.Evaluate();

        AssertResult(result, expected, clock.UtcNow);
        Assert.Equal(1, clock.ReadCount);
    }

    [Theory]
    [InlineData("2026-01-05T17:30:00Z", PickupHoursStatus.InsufficientTime)]
    [InlineData("2026-01-05T18:00:00Z", PickupHoursStatus.Closed)]
    [InlineData("2026-01-05T18:30:00Z", PickupHoursStatus.Closed)]
    [InlineData("2026-01-05T19:00:00Z", PickupHoursStatus.Eligible)]
    [InlineData("2026-01-05T22:00:00Z", PickupHoursStatus.Eligible)]
    [InlineData("2026-01-05T22:30:00Z", PickupHoursStatus.InsufficientTime)]
    public void SeparateIntervals_DoNotAllowAReservationAcrossABreak(string now, PickupHoursStatus expected)
    {
        var clock = new FixedTimeProvider(Instant(now));
        var options = Schedule(
            Interval("Monday", "09:00", "12:00"),
            Interval("Monday", "13:00", "17:00"));

        var result = Eligibility(options, clock).Evaluate();

        AssertResult(result, expected, clock.UtcNow);
    }

    [Theory]
    [InlineData("Sunday", PickupHoursStatus.Eligible)]
    [InlineData("Monday", PickupHoursStatus.Closed)]
    public void Weekday_UsesCampusDateWhenUtcHasAlreadyReachedTheNextDay(string scheduledDay, PickupHoursStatus expected)
    {
        // Monday in UTC is still Sunday at 22:00 in Chicago.
        var clock = new FixedTimeProvider(Instant("2026-01-05T04:00:00Z"));

        var result = Eligibility(Schedule(Interval(scheduledDay, "21:00", "23:30")), clock).Evaluate();

        AssertResult(result, expected, clock.UtcNow);
    }

    [Theory]
    [InlineData("2026-01-04", PickupHoursStatus.Closed)]
    [InlineData("2026-01-05", PickupHoursStatus.Eligible)]
    public void ClosedDates_OverrideTheCampusDateRatherThanTheUtcDate(string closedDate, PickupHoursStatus expected)
    {
        var clock = new FixedTimeProvider(Instant("2026-01-05T04:00:00Z"));
        var options = Schedule(Interval("Sunday", "21:00", "23:30"));
        options.ClosedDates.Add(closedDate);

        var result = Eligibility(options, clock).Evaluate();

        AssertResult(result, expected, clock.UtcNow);
    }

    [Theory]
    [InlineData("2026-01-05T15:00:00Z")]
    [InlineData("2026-07-06T14:00:00Z")]
    public void CampusTimeZone_UsesWinterAndSummerOffsetsForTheSameLocalOpening(string now)
    {
        var clock = new FixedTimeProvider(Instant(now));

        var result = Eligibility(Schedule(Interval("Monday", "09:00", "10:00")), clock).Evaluate();

        AssertResult(result, PickupHoursStatus.Eligible, clock.UtcNow);
    }

    [Theory]
    [InlineData("2026-03-08T07:30:00Z", "04:00", PickupHoursStatus.Eligible, "2026-03-08T08:30:00Z")]
    [InlineData("2026-03-08T07:30:00Z", "03:00", PickupHoursStatus.InsufficientTime, null)]
    [InlineData("2026-11-01T06:30:00Z", "02:00", PickupHoursStatus.Eligible, "2026-11-01T07:30:00Z")]
    [InlineData("2026-11-01T07:30:00Z", "02:00", PickupHoursStatus.InsufficientTime, null)]
    public void DaylightSavingChanges_UseElapsedTimeInsteadOfAddingOneLocalClockHour(
        string now, string closesAt, PickupHoursStatus expected, string? expiresAt)
    {
        var clock = new FixedTimeProvider(Instant(now));
        var options = Schedule(Interval("Sunday", "00:00", closesAt));

        var result = Eligibility(options, clock).Evaluate();

        AssertResult(result, expected, clock.UtcNow);
        if (expiresAt is not null)
        {
            Assert.Equal(Instant(expiresAt), result.ExpiresAtUtc);
        }
    }

    [Theory]
    [InlineData("2026-03-08T07:00:00Z", "02:15", "04:00")]
    [InlineData("2026-03-08T07:00:00Z", "00:00", "02:30")]
    [InlineData("2026-11-01T05:00:00Z", "01:00", "03:00")]
    [InlineData("2026-11-01T05:00:00Z", "00:00", "01:30")]
    public void NonexistentOrAmbiguousLocalEndpoints_MakeTheScheduleUnavailable(
        string now, string opensAt, string closesAt)
    {
        var clock = new FixedTimeProvider(Instant(now));

        var result = Eligibility(Schedule(Interval("Sunday", opensAt, closesAt)), clock).Evaluate();

        AssertResult(result, PickupHoursStatus.ScheduleUnavailable, clock.UtcNow);
    }

    [Theory]
    [InlineData("empty schedule")]
    [InlineData("null schedule")]
    [InlineData("null interval")]
    [InlineData("empty timezone")]
    [InlineData("unknown timezone")]
    [InlineData("invalid day")]
    [InlineData("numeric day")]
    [InlineData("empty day")]
    [InlineData("un-padded opening time")]
    [InlineData("opening time with seconds")]
    [InlineData("invalid closing time")]
    [InlineData("equal endpoints")]
    [InlineData("overnight interval")]
    [InlineData("overlapping intervals")]
    [InlineData("touching intervals")]
    [InlineData("invalid date format")]
    [InlineData("nonexistent date")]
    [InlineData("null closed dates")]
    [InlineData("null closed date")]
    [InlineData("invalid interval on another day")]
    public void InvalidConfiguration_FailsClosedWithoutReturningReservationTimes(string scenario)
    {
        var clock = new FixedTimeProvider(Instant("2026-01-05T15:00:00Z"));
        var options = Schedule(Interval("Monday", "09:00", "12:00"));
        switch (scenario)
        {
            case "empty schedule": options.WeeklyPickupIntervals.Clear(); break;
            case "null schedule": options.WeeklyPickupIntervals = null!; break;
            case "null interval": options.WeeklyPickupIntervals.Add(null!); break;
            case "empty timezone": options.CampusTimeZone = "  "; break;
            case "unknown timezone": options.CampusTimeZone = "Unknown/Campus"; break;
            case "invalid day": options.WeeklyPickupIntervals[0].Day = "Funday"; break;
            case "numeric day": options.WeeklyPickupIntervals[0].Day = "1"; break;
            case "empty day": options.WeeklyPickupIntervals[0].Day = ""; break;
            case "un-padded opening time": options.WeeklyPickupIntervals[0].OpensAt = "9:00"; break;
            case "opening time with seconds": options.WeeklyPickupIntervals[0].OpensAt = "09:00:00"; break;
            case "invalid closing time": options.WeeklyPickupIntervals[0].ClosesAt = "25:00"; break;
            case "equal endpoints": options.WeeklyPickupIntervals[0].ClosesAt = "09:00"; break;
            case "overnight interval": options.WeeklyPickupIntervals[0] = Interval("Monday", "22:00", "02:00"); break;
            case "overlapping intervals": options.WeeklyPickupIntervals.Add(Interval("Monday", "11:00", "15:00")); break;
            case "touching intervals": options.WeeklyPickupIntervals.Add(Interval("Monday", "12:00", "15:00")); break;
            case "invalid date format": options.ClosedDates.Add("01/05/2026"); break;
            case "nonexistent date": options.ClosedDates.Add("2026-02-30"); break;
            case "null closed dates": options.ClosedDates = null!; break;
            case "null closed date": options.ClosedDates.Add(null!); break;
            case "invalid interval on another day": options.WeeklyPickupIntervals.Add(Interval("Tuesday", "invalid", "12:00")); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var result = Eligibility(options, clock).Evaluate();

        AssertResult(result, PickupHoursStatus.ScheduleUnavailable, clock.UtcNow);
    }

    [Fact]
    public void ScheduleText_AllowsSurroundingWhitespaceAndCaseInsensitiveDayNames()
    {
        var clock = new FixedTimeProvider(Instant("2026-01-05T15:00:00Z"));
        var options = Schedule(Interval(" monday ", " 09:00 ", " 12:00 "));
        options.CampusTimeZone = " America/Chicago ";
        options.ClosedDates.Add(" 2026-01-06 ");

        var result = Eligibility(options, clock).Evaluate();

        AssertResult(result, PickupHoursStatus.Eligible, clock.UtcNow);
    }

    private static PickupHoursEligibility Eligibility(ReservationOptions options, TimeProvider clock)
        => new(Options.Create(options), clock);

    private static ReservationOptions Schedule(params WeeklyPickupInterval[] intervals)
        => new() { CampusTimeZone = "America/Chicago", WeeklyPickupIntervals = [.. intervals] };

    private static WeeklyPickupInterval Interval(string day, string opensAt, string closesAt)
        => new() { Day = day, OpensAt = opensAt, ClosesAt = closesAt };

    private static DateTimeOffset Instant(string text)
        => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static void AssertResult(PickupHoursResult result, PickupHoursStatus expected, DateTimeOffset now)
    {
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == PickupHoursStatus.Eligible, result.IsEligible);
        if (expected == PickupHoursStatus.Eligible)
        {
            Assert.Equal(now, result.StartsAtUtc);
            Assert.Equal(now.AddHours(1), result.ExpiresAtUtc);
            Assert.Equal(TimeSpan.Zero, result.StartsAtUtc!.Value.Offset);
            Assert.Equal(TimeSpan.Zero, result.ExpiresAtUtc!.Value.Offset);
        }
        else
        {
            Assert.Null(result.StartsAtUtc);
            Assert.Null(result.ExpiresAtUtc);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return UtcNow;
        }
    }
}
