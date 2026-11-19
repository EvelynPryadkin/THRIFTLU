using System.Globalization;
using LUThrift.Web.Configuration;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Services;

public sealed class CampusTimeFormatter
{
    private readonly TimeZoneInfo campusTimeZone = TimeZoneInfo.Utc;
    private readonly PickupScheduleService? schedules;
    private Task<CampusTimeFormatter>? capturedFormatter;

    public CampusTimeFormatter(PickupScheduleService schedules)
    {
        this.schedules = schedules;
    }

    public CampusTimeFormatter(IOptions<ReservationOptions> options)
    {
        var timeZoneId = options.Value.CampusTimeZone;
        if (string.IsNullOrWhiteSpace(timeZoneId)) return;

        try
        {
            campusTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Previously saved reservations remain readable if configuration changes.
        }
    }

    public string Format(DateTime utc)
    {
        if (schedules is not null)
            throw new InvalidOperationException("Use asynchronous formatting with the persisted pickup schedule.");
        // Stored timestamps are UTC even when SQLite returns an Unspecified kind.
        var instant = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        var local = TimeZoneInfo.ConvertTime(instant, campusTimeZone);
        return $"{local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} "
            + $"({campusTimeZone.Id}, UTC{local.ToString("zzz", CultureInfo.InvariantCulture)})";
    }

    public async Task<string> FormatAsync(DateTime utc, CancellationToken cancellationToken = default)
    {
        if (schedules is null) return Format(utc);
        // The scoped formatter uses one timezone for all timestamps in a response.
        capturedFormatter ??= CaptureAsync(cancellationToken);
        return (await capturedFormatter).Format(utc);
    }

    private async Task<CampusTimeFormatter> CaptureAsync(CancellationToken cancellationToken)
    {
        var snapshot = await schedules!.GetAsync(cancellationToken);
        return new CampusTimeFormatter(Options.Create(new ReservationOptions { CampusTimeZone = snapshot.CampusTimeZone }));
    }
}
