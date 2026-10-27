using LUThrift.Web.Authorization;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

namespace LUThrift.Web.Pages.Thrift;

public class DetailsModel(
    CatalogService catalogService,
    ReservationService reservationService,
    PickupHoursEligibility pickupHours) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public CatalogFilterInput Filter { get; set; } = new();

    public CatalogListingDetails Listing { get; private set; } = null!;
    public Dictionary<string, string> CatalogRouteValues { get; private set; } = [];
    public string LoginReturnUrl { get; private set; } = string.Empty;
    public bool IsSignedIn => User.Identity?.IsAuthenticated == true;
    public bool IsEligibleStudent { get; private set; }
    public PickupHoursStatus? PickupStatus { get; private set; }
    public bool CanReserve => Listing.Status == ListingStatus.Available
        && IsEligibleStudent && PickupStatus == PickupHoursStatus.Eligible;
    public string ReservationAvailabilityMessage { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(
        [FromRoute] int id, [FromQuery] string? pageNumber, CancellationToken cancellationToken)
    {
        var listing = await catalogService.GetDetailsAsync(id, cancellationToken);
        if (listing is null)
        {
            return NotFound();
        }

        Listing = listing;
        // Rebuild a catalog link from validated selections, never from a submitted return URL.
        if (ModelState.IsValid)
        {
            CatalogRouteValues = CatalogNavigation.RouteValues(Filter, CatalogNavigation.ParsePageNumber(pageNumber));
        }

        var detailsRouteValues = new RouteValueDictionary { ["id"] = listing.Id };
        foreach (var selection in CatalogRouteValues)
        {
            detailsRouteValues[selection.Key] = selection.Value;
        }
        LoginReturnUrl = Url.Page("/Thrift/Details", detailsRouteValues)
            ?? Url.Content($"~/Thrift/Details/{listing.Id}");

        if (IsSignedIn && User.IsInRole(ApplicationRoles.Student))
        {
            IsEligibleStudent = await reservationService.IsEligibleStudentAsync(User, cancellationToken);
        }

        if (listing.Status == ListingStatus.Held)
        {
            ReservationAvailabilityMessage = "This item is already reserved.";
        }
        else
        {
            // This is only a read-only availability hint; the POST rechecks every rule.
            PickupStatus = pickupHours.Evaluate().Status;
            ReservationAvailabilityMessage = PickupStatus switch
            {
                PickupHoursStatus.Eligible => "Reservations last for one hour. Your pickup deadline will be confirmed after you reserve.",
                PickupHoursStatus.Closed => "Pickup is currently closed. Please return during pickup hours.",
                PickupHoursStatus.InsufficientTime => "There isn't enough pickup time left for a full one-hour reservation. Please return during the next pickup period.",
                _ => "Reservations are unavailable because valid pickup hours have not been configured. Please check back later."
            };
        }
        return Page();
    }
}
