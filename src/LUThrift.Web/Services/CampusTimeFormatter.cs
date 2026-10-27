using System.Globalization;
using LUThrift.Web.Configuration;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Services;

public sealed class CampusTimeFormatter
{
    private readonly TimeZoneInfo campusTimeZone = TimeZoneInfo.Utc;

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
        // Stored timestamps are UTC even when SQLite returns an Unspecified kind.
        var instant = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        var local = TimeZoneInfo.ConvertTime(instant, campusTimeZone);
        return $"{local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} "
            + $"({campusTimeZone.Id}, UTC{local.ToString("zzz", CultureInfo.InvariantCulture)})";
    }
}
