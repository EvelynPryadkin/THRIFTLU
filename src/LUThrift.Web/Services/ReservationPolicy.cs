using LUThrift.Web.Models;

namespace LUThrift.Web.Services;

public sealed class ReservationPolicy(TimeProvider timeProvider)
{
    public static TimeSpan Duration { get; } = TimeSpan.FromHours(1);
    public const int MaximumActiveReservations = 3;

    public bool IsActive(Reservation reservation)
        => reservation.Status == ReservationStatus.Active
            && reservation.ExpiresAtUtc > timeProvider.GetUtcNow().UtcDateTime;
}
