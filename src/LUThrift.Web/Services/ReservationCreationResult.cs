namespace LUThrift.Web.Services;

public enum ReservationCreationStatus
{
    Success,
    InvalidInput,
    AuthenticationRequired,
    StudentRequired,
    NotFound,
    Unavailable,
    AlreadyReserved,
    PickupUnavailable,
    LimitReached,
    Conflict,
    PersistenceFailed
}

public sealed record ReservationCreationResult(
    ReservationCreationStatus Status,
    string Message,
    int? ReservationId = null,
    DateTime? ExpiresAtUtc = null)
{
    public bool Succeeded => Status == ReservationCreationStatus.Success;
}
