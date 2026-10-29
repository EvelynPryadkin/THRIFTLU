using LUThrift.Web.Models;

namespace LUThrift.Web.ViewModels;

// Status reflects the captured query time, including overdue rows awaiting cleanup.
// A future pickup action must recheck both Active/unexpired and Held under its locks.
public sealed record StaffPickupSummary(
    int ReservationId,
    string ItemTitle,
    string StudentDisplayName,
    string? StudentEmail,
    ReservationStatus ReservationStatus,
    ListingStatus ListingStatus,
    DateTime ExpiresAtUtc);
