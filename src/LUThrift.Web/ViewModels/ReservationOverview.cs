namespace LUThrift.Web.ViewModels;

public sealed record ReservationOverview(
    IReadOnlyList<ReservationSummary> Current,
    IReadOnlyList<ReservationSummary> RecentHistory)
{
    public const int RecentHistoryLimit = 20;
    public int ActiveCount => Current.Count;
}
