using LUThrift.Web.ViewModels;

namespace LUThrift.Web.Services;

public enum StaffAccessQueryStatus
{
    Success,
    AuthenticationRequired,
    AdministratorRequired,
    InvalidInput
}

public sealed record StaffAccessQueryResult(
    StaffAccessQueryStatus Status,
    string Message,
    StaffAccessPage? Page = null)
{
    public bool Succeeded => Status == StaffAccessQueryStatus.Success;
}

public enum StaffAccessAction
{
    Grant = 1,
    Remove = 2
}

public enum StaffAccessOperationStatus
{
    Changed,
    Unchanged,
    AuthenticationRequired,
    AdministratorRequired,
    InvalidInput,
    NotFound,
    ProtectedAccount,
    Conflict,
    PersistenceFailed
}

public sealed record StaffAccessOperationResult(StaffAccessOperationStatus Status, string Message)
{
    public bool Succeeded => Status is StaffAccessOperationStatus.Changed or StaffAccessOperationStatus.Unchanged;
}
