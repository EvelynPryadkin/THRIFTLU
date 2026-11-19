using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

internal static class PickupScheduleTestData
{
    // Explicit fixture setup only: the production host never imports settings at startup.
    public static async Task PersistConfiguredAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ReservationOptions>>().Value;
        var validated = PickupScheduleValidator.Validate(options);
        Assert.True(validated.IsValid);
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await database.PickupSchedules.AnyAsync());
        database.PickupSchedules.Add(new PickupSchedule
        {
            Version = Guid.NewGuid(), CampusTimeZone = validated.TimeZone!.Id,
            WeeklyIntervals = validated.Intervals.Select(interval => new PickupScheduleInterval
            {
                Day = interval.Day, OpensAt = interval.OpensAt, ClosesAt = interval.ClosesAt
            }).ToList(),
            ClosedDates = validated.ClosedDates.Select(date => new PickupScheduleClosedDate { Date = date }).ToList()
        });
        await database.SaveChangesAsync();
    }
}
