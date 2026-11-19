namespace LUThrift.Web.Services;

public enum PickupScheduleOperationStatus
{
    Saved,
    Unchanged,
    AuthenticationRequired,
    AdministratorRequired,
    InvalidInput,
    Conflict,
    PersistenceFailed
}

public sealed record PickupScheduleOperationResult(
    PickupScheduleOperationStatus Status,
    string Message,
    Guid? Version = null)
{
    public bool Succeeded => Status is PickupScheduleOperationStatus.Saved or PickupScheduleOperationStatus.Unchanged;
}
