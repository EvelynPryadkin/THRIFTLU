using LUThrift.Web.Models;

namespace LUThrift.Web.ViewModels;

// Status reflects the captured query time, including overdue rows awaiting cleanup.
// CanConfirmPickup is a display hint; the POST rechecks eligibility under its locks.
public sealed record StaffPickupSummary(
    int ReservationId,
    string ItemTitle,
    string StudentDisplayName,
    string? StudentEmail,
    ReservationStatus ReservationStatus,
    ListingStatus ListingStatus,
    DateTime ExpiresAtUtc,
    bool CanConfirmPickup);
