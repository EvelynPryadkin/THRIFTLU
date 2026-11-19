namespace LUThrift.Web.ViewModels;

public sealed record ActivityHistoryEntry(
    int Id, string ActionLabel, string Summary, string ActorId, string? ActorDisplayName,
    int? ListingId, string? ListingTitle, string? TargetUserId, string? TargetDisplayName,
    int? ScheduleId, DateTime OccurredAtUtc);

public sealed record ActivityHistoryPage(IReadOnlyList<ActivityHistoryEntry> Items, int TotalCount, int PageNumber)
{
    public const int PageSize = 20;
    public int TotalPages => TotalCount == 0 ? 1 : (TotalCount - 1) / PageSize + 1;
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}
