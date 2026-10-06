using System.ComponentModel.DataAnnotations;

namespace LUThrift.Web.InputModels;

public class CreateListingInput
{
    [Required]
    [StringLength(150)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(2000)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string Condition { get; set; } = string.Empty;

    [StringLength(50)]
    public string? Size { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Choose a category.")]
    [Display(Name = "Category")]
    public int CategoryId { get; set; }
}
