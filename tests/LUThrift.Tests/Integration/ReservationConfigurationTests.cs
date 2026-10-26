using System.Net;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LUThrift.Tests.Integration;

public sealed class ReservationConfigurationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("timezone")]
    [InlineData("time")]
    [InlineData("closed-date")]
    public async Task MissingOrInvalidHours_DisableEligibilityWithoutBreakingPublicBrowsing(string invalidSetting)
    {
        using var original = new AuthorizationWebApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            if (invalidSetting != "missing")
            {
                var values = new Dictionary<string, string?>
                {
                    ["Reservations:CampusTimeZone"] = invalidSetting == "timezone" ? "not-a-timezone" : "America/Chicago",
                    ["Reservations:WeeklyPickupIntervals:0:Day"] = "Monday",
                    ["Reservations:WeeklyPickupIntervals:0:OpensAt"] = invalidSetting == "time" ? "not-a-time" : "10:00",
                    ["Reservations:WeeklyPickupIntervals:0:ClosesAt"] = "14:00"
                };
                if (invalidSetting == "closed-date")
                {
                    values["Reservations:ClosedDates:0"] = "not-a-date";
                }
                config.AddInMemoryCollection(values);
            }
        }));
        using var client = factory.CreateClient();

        var result = factory.Services.GetRequiredService<PickupHoursEligibility>().Evaluate();
        Assert.Equal(PickupHoursStatus.ScheduleUnavailable, result.Status);
        Assert.Null(result.StartsAtUtc);
        Assert.Null(result.ExpiresAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Thrift")).StatusCode);
    }

    [Fact]
    public void ConfiguredSchedule_UsesInjectedTimeAndFixedPolicyInsteadOfExtraConfigurationValues()
    {
        // Synthetic test hours, not the room's official schedule.
        var now = new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
        using var original = new AuthorizationWebApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reservations:CampusTimeZone"] = "America/Chicago",
                ["Reservations:WeeklyPickupIntervals:0:Day"] = "Monday",
                ["Reservations:WeeklyPickupIntervals:0:OpensAt"] = "10:00",
                ["Reservations:WeeklyPickupIntervals:0:ClosesAt"] = "14:00",
                ["Reservations:DurationMinutes"] = "120",
                ["Reservations:MaximumActiveReservations"] = "99"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            });
        });

        var result = factory.Services.GetRequiredService<PickupHoursEligibility>().Evaluate();
        Assert.True(result.IsEligible);
        Assert.Equal(now, result.StartsAtUtc);
        Assert.Equal(now.AddHours(1), result.ExpiresAtUtc);
        Assert.Equal(3, ReservationPolicy.MaximumActiveReservations);
        Assert.False(factory.Services.GetRequiredService<ReservationPolicy>().IsActive(new Reservation
        {
            ExpiresAtUtc = now.UtcDateTime
        }));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
