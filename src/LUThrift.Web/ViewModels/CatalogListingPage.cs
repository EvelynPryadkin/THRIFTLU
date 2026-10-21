namespace LUThrift.Web.ViewModels;

public sealed record CatalogListingPage(
    IReadOnlyList<CatalogListingSummary> Items,
    int TotalCount,
    int PageNumber)
{
    public const int PageSize = 12;

    // Avoid count + pageSize - 1, which could overflow for a large count.
    public int TotalPages => TotalCount == 0 ? 1 : (TotalCount - 1) / PageSize + 1;
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}
