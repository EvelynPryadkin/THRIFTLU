using LUThrift.Web.Models;

namespace LUThrift.Web.ViewModels;

public sealed record CatalogListingSummary(
    int Id,
    string Title,
    string CategoryName,
    string Condition,
    string? Size,
    ListingStatus Status);
