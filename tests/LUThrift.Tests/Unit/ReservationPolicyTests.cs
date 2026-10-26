using LUThrift.Web.Models;
using LUThrift.Web.Services;

namespace LUThrift.Tests.Unit;

public sealed class ReservationPolicyTests
{
    [Fact]
    public void ActiveReservation_StopsCountingAtTheExactDeadlineUsingTheInjectedClock()
    {
        var deadline = new DateTimeOffset(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
        var clock = new ControlledTimeProvider(deadline.AddTicks(-1));
        var policy = new ReservationPolicy(clock);
        var reservation = new Reservation
        {
            Status = ReservationStatus.Active,
            ExpiresAtUtc = deadline.UtcDateTime
        };

        Assert.True(policy.IsActive(reservation));
        clock.UtcNow = deadline;
        Assert.False(policy.IsActive(reservation));
        clock.UtcNow = deadline.AddTicks(1);
        Assert.False(policy.IsActive(reservation));
        // The clock check does not persist expiration or release a listing.
        Assert.Equal(ReservationStatus.Active, reservation.Status);
    }

    [Theory]
    [InlineData(ReservationStatus.Canceled)]
    [InlineData(ReservationStatus.Expired)]
    [InlineData(ReservationStatus.Fulfilled)]
    public void NonActiveStatus_DoesNotCountEvenBeforeTheDeadline(ReservationStatus status)
    {
        var now = new DateTimeOffset(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
        var policy = new ReservationPolicy(new ControlledTimeProvider(now));

        Assert.False(policy.IsActive(new Reservation
        {
            Status = status,
            ExpiresAtUtc = now.AddHours(1).UtcDateTime
        }));
    }

    [Fact]
    public void UtcDatabaseTimestamp_WithUnspecifiedKindIsNotReinterpretedAsMachineLocalTime()
    {
        var now = new DateTimeOffset(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
        var policy = new ReservationPolicy(new ControlledTimeProvider(now));
        var reservation = new Reservation
        {
            ExpiresAtUtc = DateTime.SpecifyKind(now.AddTicks(1).UtcDateTime, DateTimeKind.Unspecified)
        };

        Assert.True(policy.IsActive(reservation));
        reservation.ExpiresAtUtc = DateTime.SpecifyKind(now.UtcDateTime, DateTimeKind.Unspecified);
        Assert.False(policy.IsActive(reservation));
    }

    private sealed class ControlledTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
