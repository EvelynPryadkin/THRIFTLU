using LUThrift.Web.Models;

namespace LUThrift.Web.ViewModels;

public sealed record ReservationSummary(
    int Id, int ListingId, string Title, DateTime CreatedAtUtc, DateTime ExpiresAtUtc,
    ReservationStatus Status, bool CanCancel);
