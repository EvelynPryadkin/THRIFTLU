using LUThrift.Web.Models;

namespace LUThrift.Web.ViewModels;

public sealed record CatalogListingDetails(
    int Id,
    string Title,
    string Description,
    string CategoryName,
    string Condition,
    string? Size,
    ListingStatus Status);
