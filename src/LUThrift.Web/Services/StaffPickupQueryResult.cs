using LUThrift.Web.ViewModels;

namespace LUThrift.Web.Services;

public enum StaffPickupQueryStatus
{
    Success,
    AuthenticationRequired,
    StaffRequired,
    InvalidInput
}

public sealed record StaffPickupQueryResult(
    StaffPickupQueryStatus Status,
    string Message,
    StaffPickupPage? Page = null)
{
    public bool Succeeded => Status == StaffPickupQueryStatus.Success;
}
