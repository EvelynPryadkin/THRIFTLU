using System.Collections.ObjectModel;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;

namespace LUThrift.Web.Services;

// Only controlled descriptions are shown. Neither free-form audit details nor an
// unknown action's raw value is safe to treat as presentation text.
public static class ActivityHistoryPresentation
{
    private static readonly IReadOnlyDictionary<string, string> inventory = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [InventoryAuditActions.Created] = "Listing created",
            [InventoryAuditActions.Updated] = "Listing updated",
            [InventoryAuditActions.Published] = "Listing published",
            [InventoryAuditActions.MarkedMissing] = "Listing marked missing",
            [InventoryAuditActions.WalkInCollected] = "Walk-in collection recorded",
            [InventoryAuditActions.Archived] = "Listing archived",
            [InventoryAuditActions.Reserved] = "Reservation created",
            [InventoryAuditActions.ReservationCanceled] = "Reservation canceled",
            [InventoryAuditActions.ReservationExpired] = "Reservation expired",
            [InventoryAuditActions.ReservationFulfilled] = "Reservation fulfilled",
            [InventoryAuditActions.ReservationItemMissing] = "Reserved item reported missing",
            [InventoryAuditActions.ImageAdded] = "Photo added",
            [InventoryAuditActions.ImageReplaced] = "Photo replaced",
            [InventoryAuditActions.ImageRemoved] = "Photo removed"
        });
    private static readonly IReadOnlyDictionary<string, string> administration = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AdministrativeAuditActions.StaffGranted] = "Staff access granted",
            [AdministrativeAuditActions.StaffRemoved] = "Staff access removed",
            [AdministrativeAuditActions.PickupScheduleSaved] = "Pickup schedule saved",
            [AdministrativeAuditActions.PickupScheduleImported] = "Pickup schedule imported"
        });

    public static IReadOnlyDictionary<string, string> Actions(ActivityHistoryKind kind)
        => kind == ActivityHistoryKind.Administration ? administration : inventory;

    public static string Label(ActivityHistoryKind kind, string action)
        => Actions(kind).TryGetValue(action, out var label) ? label : "Unrecognized action";

    public static string Summary(ActivityHistoryKind kind, string action)
    {
        if (!Actions(kind).ContainsKey(action)) return "No safe summary available.";
        return action switch
        {
            InventoryAuditActions.Created => "A Draft listing was created.",
            InventoryAuditActions.Updated => "Editable listing information was updated.",
            InventoryAuditActions.Published => "The listing was made Available.",
            InventoryAuditActions.MarkedMissing => "The listing was marked Missing.",
            InventoryAuditActions.WalkInCollected => "The listing was marked Collected after a walk-in collection.",
            InventoryAuditActions.Archived => "The listing was archived; its history was retained.",
            InventoryAuditActions.Reserved => "A reservation was created and the listing was placed on hold.",
            InventoryAuditActions.ReservationCanceled => "The reservation was canceled.",
            InventoryAuditActions.ReservationExpired => "The reservation reached its deadline and was recorded as Expired.",
            InventoryAuditActions.ReservationFulfilled => "The reservation was fulfilled and the listing was marked Collected.",
            InventoryAuditActions.ReservationItemMissing => "The reservation was canceled because the item was missing; the listing was marked Missing.",
            InventoryAuditActions.ImageAdded => "A listing photo was added.",
            InventoryAuditActions.ImageReplaced => "The listing photo was replaced.",
            InventoryAuditActions.ImageRemoved => "The listing photo was removed.",
            AdministrativeAuditActions.StaffGranted => "Staff membership was added to the affected account.",
            AdministrativeAuditActions.StaffRemoved => "Staff membership was removed from the affected account.",
            AdministrativeAuditActions.PickupScheduleSaved => "The pickup schedule was saved.",
            AdministrativeAuditActions.PickupScheduleImported => "Existing configured pickup hours were explicitly imported.",
            _ => "No safe summary available."
        };
    }
}
