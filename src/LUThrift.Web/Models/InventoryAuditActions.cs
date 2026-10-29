namespace LUThrift.Web.Models;

public static class InventoryAuditActions
{
    public const string Created = "ListingCreated";
    public const string Updated = "ListingUpdated";
    public const string Published = "ListingPublished";
    public const string MarkedMissing = "ListingMarkedMissing";
    public const string WalkInCollected = "ListingWalkInCollected";
    public const string Archived = "ListingArchived";
    public const string Reserved = "ListingReserved";
    public const string ReservationCanceled = "ReservationCanceled";
    public const string ReservationExpired = "ReservationExpired";
    public const string ReservationFulfilled = "ReservationFulfilled";
}
