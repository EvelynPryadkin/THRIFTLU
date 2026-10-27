namespace LUThrift.Web.Services;

public enum ReservationOperationStatus
{
    Canceled,
    Expired,
    NotFound,
    NotActive,
    NotDue,
    AuthenticationRequired,
    StudentRequired,
    Conflict,
    PersistenceFailed
}

public sealed record ReservationOperationResult(ReservationOperationStatus Status, string Message)
{
    public bool Changed => Status is ReservationOperationStatus.Canceled or ReservationOperationStatus.Expired;
}

public sealed record ReservationExpirationBatchResult(int Processed, int Failed);
