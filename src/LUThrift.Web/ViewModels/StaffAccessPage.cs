namespace LUThrift.Web.ViewModels;

public sealed record StaffAccessAccount(
    string Id,
    string DisplayName,
    string? Email,
    bool IsActive,
    bool IsStaff,
    bool IsAdministrator);

public sealed record StaffAccessPage(
    IReadOnlyList<StaffAccessAccount> Items,
    int TotalCount,
    int PageNumber)
{
    public const int PageSize = 20;

    public int TotalPages => TotalCount == 0 ? 1 : (TotalCount - 1) / PageSize + 1;
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}
