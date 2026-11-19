using System.ComponentModel.DataAnnotations;

namespace LUThrift.Web.InputModels;

public sealed class StaffAccessQueryInput
{
    public const int MaximumSearchLength = 100;
    private string? search;

    [Display(Name = "Name or email")]
    [StringLength(MaximumSearchLength, ErrorMessage = "Keep the search to 100 characters or fewer.")]
    [RegularExpression(@"[^\p{Cc}]*", ErrorMessage = "Enter valid search text.")]
    public string? Search
    {
        get => search;
        set => search = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
