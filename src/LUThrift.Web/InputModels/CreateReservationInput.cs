using System.ComponentModel.DataAnnotations;

namespace LUThrift.Web.InputModels;

public sealed class CreateReservationInput
{
    [Range(1, int.MaxValue, ErrorMessage = "Choose a valid listing.")]
    public int ListingId { get; set; }
}
