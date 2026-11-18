using Microsoft.AspNetCore.Authorization;

namespace LUThrift.Web.Authorization;

public enum CurrentAccountPermission
{
    ActiveAccount,
    StaffOrAdministrator,
    Administrator
}

public sealed record CurrentAccountRequirement(CurrentAccountPermission Permission) : IAuthorizationRequirement;
