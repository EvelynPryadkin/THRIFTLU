using System.ComponentModel.DataAnnotations;

namespace LUThrift.Web.InputModels;

public sealed class StaffPickupQueryInput
{
    public const int MaximumEmailSearchLength = 256;
    private string? studentEmail;

    [Display(Name = "Reservation ID")]
    [Range(1, int.MaxValue, ErrorMessage = "Enter a positive reservation ID.")]
    public int? ReservationId { get; set; }

    [Display(Name = "Student email")]
    [StringLength(MaximumEmailSearchLength, ErrorMessage = "Keep the email search to 256 characters or fewer.")]
    [RegularExpression(@"[^\p{Cc}]*", ErrorMessage = "Enter valid email search text.")]
    public string? StudentEmail
    {
        get => studentEmail;
        set => studentEmail = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
