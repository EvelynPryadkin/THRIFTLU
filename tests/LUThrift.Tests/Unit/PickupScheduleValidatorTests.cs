using System.Globalization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Services;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Unit;

public class PickupScheduleValidatorTests
{
    // These hours are synthetic validation fixtures, not an official pickup schedule.
    [Fact]
    public void EmptySchedule_IsValidButDoesNotEnableReservations()
    {
        var settings = new ReservationOptions();

        var result = PickupScheduleValidator.Validate(settings);

        Assert.True(result.IsValid);
        Assert.False(result.IsConfigured);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Intervals);
        Assert.Equal(PickupHoursStatus.ScheduleUnavailable,
            new PickupHoursEligibility(Options.Create(settings), TimeProvider.System).Evaluate().Status);
    }

    [Fact]
    public void Normalization_SortsIntervalsAndDeduplicatesDatesWithoutChangingInput()
    {
        var settings = new ReservationOptions
        {
            CampusTimeZone = " America/Chicago ",
            WeeklyPickupIntervals =
            [
                Interval("Wednesday", "13:00", "15:00"),
                Interval(" monday ", " 13:00 ", " 15:00 "),
                Interval("Monday", "09:00", "12:00")
            ],
            ClosedDates = ["2026-12-25", " 2026-01-01 ", "2026-12-25"]
        };

        var result = PickupScheduleValidator.Validate(settings);

        Assert.True(result.IsConfigured);
        Assert.Equal("America/Chicago", result.TimeZone!.Id);
        Assert.Equal(
            new[]
            {
                new ValidatedPickupInterval(DayOfWeek.Monday, new(9, 0), new(12, 0)),
                new ValidatedPickupInterval(DayOfWeek.Monday, new(13, 0), new(15, 0)),
                new ValidatedPickupInterval(DayOfWeek.Wednesday, new(13, 0), new(15, 0))
            }, result.Intervals);
        Assert.Equal(new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25) }, result.ClosedDates);
        Assert.Equal("Wednesday", settings.WeeklyPickupIntervals[0].Day);
        Assert.Equal(" monday ", settings.WeeklyPickupIntervals[1].Day);
        Assert.Equal(3, settings.ClosedDates.Count);
    }

    [Theory]
    [InlineData("09:00", "12:00")]
    [InlineData("11:00", "13:00")]
    [InlineData("12:00", "14:00")]
    public void DuplicateOverlappingOrTouchingIntervals_AreInvalid(string opening, string closing)
    {
        var settings = new ReservationOptions
        {
            WeeklyPickupIntervals = [Interval("Monday", "09:00", "12:00"), Interval("Monday", opening, closing)]
        };

        AssertInvalid(PickupScheduleValidator.Validate(settings));
    }

    [Fact]
    public void MaximumCollectionSizes_AreAcceptedButAdditionalEntriesAreRejected()
    {
        var settings = new ReservationOptions
        {
            WeeklyPickupIntervals = Enum.GetValues<DayOfWeek>().SelectMany(day =>
                Enumerable.Range(0, 6).Select(index => Interval(day.ToString(),
                    new TimeOnly(index * 3, 0).ToString("HH:mm", CultureInfo.InvariantCulture),
                    new TimeOnly(index * 3 + 1, 0).ToString("HH:mm", CultureInfo.InvariantCulture)))).ToList(),
            ClosedDates = Enumerable.Range(0, PickupScheduleValidator.MaximumClosedDates)
                .Select(offset => new DateOnly(2026, 1, 1).AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList()
        };
        Assert.Equal(PickupScheduleValidator.MaximumWeeklyIntervals, settings.WeeklyPickupIntervals.Count);
        Assert.True(PickupScheduleValidator.Validate(settings).IsValid);

        settings.WeeklyPickupIntervals.Add(Interval("Monday", "20:00", "22:00"));
        AssertInvalid(PickupScheduleValidator.Validate(settings));
        settings.WeeklyPickupIntervals.RemoveAt(settings.WeeklyPickupIntervals.Count - 1);
        settings.ClosedDates.Add("2028-01-01");
        AssertInvalid(PickupScheduleValidator.Validate(settings));
    }

    [Fact]
    public void NullCollectionsAndEntries_AreInvalidRatherThanAnEmptySchedule()
    {
        AssertInvalid(PickupScheduleValidator.Validate(new() { WeeklyPickupIntervals = null! }));
        AssertInvalid(PickupScheduleValidator.Validate(new() { ClosedDates = null! }));
        AssertInvalid(PickupScheduleValidator.Validate(new() { WeeklyPickupIntervals = [null!] }));
        AssertInvalid(PickupScheduleValidator.Validate(new() { ClosedDates = [null!] }));
    }

    [Theory]
    [InlineData("1", "09:00", "12:00")]
    [InlineData("Monday", "9:00", "12:00")]
    [InlineData("Monday", "09:00:00", "12:00")]
    [InlineData("Monday", "09:00", "25:00")]
    [InlineData("Monday", "12:00", "12:00")]
    [InlineData("Monday", "22:00", "02:00")]
    [InlineData("Wednesday-extra", "09:00", "12:00")]
    public void InvalidIntervalText_IsRejected(string day, string opening, string closing)
    {
        AssertInvalid(PickupScheduleValidator.Validate(new()
        {
            WeeklyPickupIntervals = [Interval(day, opening, closing)]
        }));
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2026-1-01")]
    [InlineData("2026-01-01-extra")]
    public void InvalidClosedDate_IsRejected(string date)
    {
        AssertInvalid(PickupScheduleValidator.Validate(new() { ClosedDates = [date] }));
    }

    [Fact]
    public void InvalidTimeZoneAndOversizedText_ProduceSafeErrorsWithoutEchoingInput()
    {
        var untrustedText = new string('X', PickupScheduleValidator.MaximumTimeZoneLength + 1);
        foreach (var timeZone in new[] { "", "Not/AZone", untrustedText })
        {
            var result = PickupScheduleValidator.Validate(new() { CampusTimeZone = timeZone });
            AssertInvalid(result);
            if (timeZone.Length > 0)
            {
                Assert.All(result.Errors, error => Assert.DoesNotContain(timeZone, error));
            }
        }
    }

    private static WeeklyPickupInterval Interval(string day, string opening, string closing) =>
        new() { Day = day, OpensAt = opening, ClosesAt = closing };

    private static void AssertInvalid(PickupScheduleValidationResult result)
    {
        Assert.False(result.IsValid);
        Assert.False(result.IsConfigured);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Intervals);
        Assert.Empty(result.ClosedDates);
    }
}
