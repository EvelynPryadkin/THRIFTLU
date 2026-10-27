using LUThrift.Web.Configuration;
using LUThrift.Web.Services;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Unit;

public sealed class CampusTimeFormatterTests
{
    [Theory]
    [InlineData(1, "2026-01-15 09:14:15 (America/Chicago, UTC-06:00)")]
    [InlineData(7, "2026-07-15 10:14:15 (America/Chicago, UTC-05:00)")]
    public void Format_UsesTheSavedInstantsWinterOrSummerOffset(int month, string expected)
    {
        var formatter = Create("America/Chicago");
        var savedUtc = new DateTime(2026, month, 15, 15, 14, 15, DateTimeKind.Utc);

        Assert.Equal(expected, formatter.Format(savedUtc));
    }

    [Fact]
    public void Format_DistinguishesRepeatedLocalTimesByTheirUtcOffsets()
    {
        var formatter = Create("America/Chicago");
        var firstOccurrence = new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc);
        var secondOccurrence = firstOccurrence.AddHours(1);

        Assert.Equal("2026-11-01 01:30:00 (America/Chicago, UTC-05:00)", formatter.Format(firstOccurrence));
        Assert.Equal("2026-11-01 01:30:00 (America/Chicago, UTC-06:00)", formatter.Format(secondOccurrence));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Format_PreservesSavedUtcValuesWithoutRecomputingTheDeadline(DateTimeKind kind)
    {
        var formatter = Create(" America/Chicago ");
        var savedUtc = new DateTime(2020, 1, 1, 18, 30, 45, kind);

        Assert.Equal("2020-01-01 12:30:45 (America/Chicago, UTC-06:00)", formatter.Format(savedUtc));
        Assert.Equal("2020-01-01 12:30:46 (America/Chicago, UTC-06:00)", formatter.Format(savedUtc.AddSeconds(1)));
        Assert.Equal("2020-01-01 12:30:45 (America/Chicago, UTC-06:00)", formatter.Format(savedUtc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Unknown/Campus")]
    public void Format_InvalidTimeZoneFallsBackToClearlyLabeledUtc(string? timeZoneId)
    {
        var formatter = Create(timeZoneId);
        var savedUtc = new DateTime(2026, 10, 26, 15, 20, 0, DateTimeKind.Unspecified);

        Assert.Equal("2026-10-26 15:20:00 (UTC, UTC+00:00)", formatter.Format(savedUtc));
    }

    private static CampusTimeFormatter Create(string? timeZoneId)
        => new(Options.Create(new ReservationOptions { CampusTimeZone = timeZoneId! }));
}
