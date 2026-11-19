using System.Net;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LUThrift.Tests.Integration;

public sealed class ReservationConfigurationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("timezone")]
    [InlineData("weekday")]
    [InlineData("overlap")]
    public async Task MissingOrInvalidPersistedHours_DisableEligibilityWithoutBreakingPublicBrowsing(string invalidSetting)
    {
        using var original = new AuthorizationWebApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Legacy settings must never silently fill in missing persisted hours.
                ["Reservations:CampusTimeZone"] = "America/Chicago",
                ["Reservations:WeeklyPickupIntervals:0:Day"] = "Monday",
                ["Reservations:WeeklyPickupIntervals:0:OpensAt"] = "10:00",
                ["Reservations:WeeklyPickupIntervals:0:ClosesAt"] = "14:00"
            })));
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (invalidSetting != "missing")
        {
            var schedule = new PickupSchedule
            {
                Version = Guid.NewGuid(), CampusTimeZone = invalidSetting == "timezone" ? "not-a-timezone" : "America/Chicago",
                WeeklyIntervals = invalidSetting == "empty" ? [] : [new PickupScheduleInterval
                {
                    Day = invalidSetting == "weekday" ? (DayOfWeek)99 : DayOfWeek.Monday,
                    OpensAt = new TimeOnly(10, 0), ClosesAt = new TimeOnly(14, 0)
                }]
            };
            if (invalidSetting == "overlap") schedule.WeeklyIntervals.Add(new PickupScheduleInterval
            {
                Day = DayOfWeek.Monday, OpensAt = new TimeOnly(13, 0), ClosesAt = new TimeOnly(16, 0)
            });
            database.PickupSchedules.Add(schedule);
            await database.SaveChangesAsync();
        }
        var result = await scope.ServiceProvider.GetRequiredService<PickupHoursEligibility>().EvaluateAsync();
        Assert.Equal(PickupHoursStatus.ScheduleUnavailable, result.Status);
        Assert.Null(result.StartsAtUtc);
        Assert.Null(result.ExpiresAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Thrift")).StatusCode);
        Assert.Empty(await database.AdministrativeAuditEvents.ToListAsync());
        Assert.Equal(invalidSetting == "missing" ? 0 : 1, await database.PickupSchedules.CountAsync());
    }

    [Fact]
    public async Task PersistedSchedule_UsesInjectedTimeAndFixedPolicyInsteadOfExtraConfigurationValues()
    {
        var now = new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
        using var original = new AuthorizationWebApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reservations:CampusTimeZone"] = "UTC",
                ["Reservations:DurationMinutes"] = "120",
                ["Reservations:MaximumActiveReservations"] = "99"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            });
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        database.PickupSchedules.Add(new PickupSchedule
        {
            Version = Guid.NewGuid(), CampusTimeZone = "America/Chicago",
            WeeklyIntervals = [new PickupScheduleInterval
            {
                Day = DayOfWeek.Monday, OpensAt = new TimeOnly(10, 0), ClosesAt = new TimeOnly(14, 0)
            }]
        });
        await database.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<PickupHoursEligibility>().EvaluateAsync();
        Assert.True(result.IsEligible);
        Assert.Equal(now, result.StartsAtUtc);
        Assert.Equal(now.AddHours(1), result.ExpiresAtUtc);
        Assert.Equal(3, ReservationPolicy.MaximumActiveReservations);
        Assert.False(scope.ServiceProvider.GetRequiredService<ReservationPolicy>().IsActive(new Reservation
        {
            ExpiresAtUtc = now.UtcDateTime
        }));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
