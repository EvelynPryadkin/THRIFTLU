using System.ComponentModel.DataAnnotations;

namespace LUThrift.Web.InputModels;

public enum CatalogAvailability
{
    Available,
    Reserved,
    [Display(Name = "All catalog items")]
    All
}

public class CatalogFilterInput
{
    private string? search;
    private string? size;

    [Display(Name = "Title search")]
    [StringLength(150, ErrorMessage = "Keep the title search to 150 characters or fewer.")]
    [RegularExpression(@"[^\u0000]*", ErrorMessage = "Enter valid search text.")]
    public string? Search
    {
        get => search;
        set => search = Normalize(value);
    }

    [Display(Name = "Category")]
    [Range(1, int.MaxValue, ErrorMessage = "Choose a valid category.")]
    public int? CategoryId { get; set; }

    [StringLength(50, ErrorMessage = "Keep the size to 50 characters or fewer.")]
    [RegularExpression(@"[^\u0000]*", ErrorMessage = "Choose a valid size.")]
    public string? Size
    {
        get => size;
        set => size = Normalize(value);
    }

    [EnumDataType(typeof(CatalogAvailability), ErrorMessage = "Choose a valid availability option.")]
    public CatalogAvailability Availability { get; set; } = CatalogAvailability.Available;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
