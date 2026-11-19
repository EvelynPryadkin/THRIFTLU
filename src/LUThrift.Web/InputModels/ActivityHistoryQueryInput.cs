using System.ComponentModel.DataAnnotations;

namespace LUThrift.Web.InputModels;

public enum ActivityHistoryKind { Inventory, Administration }

public sealed class ActivityHistoryQueryInput
{
    public const int MaximumActionLength = 50;
    public const int MaximumIdentifierLength = 450;
    private string? action;
    private string? actorId;
    private string? targetUserId;

    [EnumDataType(typeof(ActivityHistoryKind))]
    public ActivityHistoryKind History { get; set; }

    [StringLength(MaximumActionLength)]
    [Display(Name = "Action")]
    public string? Action { get => action; set => action = Clean(value); }

    [StringLength(MaximumIdentifierLength)]
    [RegularExpression(@"[^\p{Cc}]*", ErrorMessage = "Enter a valid actor identifier.")]
    [Display(Name = "Actor ID")]
    public string? ActorId { get => actorId; set => actorId = Clean(value); }

    [Range(1, int.MaxValue)]
    [Display(Name = "Listing ID")]
    public int? ListingId { get; set; }

    [StringLength(MaximumIdentifierLength)]
    [RegularExpression(@"[^\p{Cc}]*", ErrorMessage = "Enter a valid affected account identifier.")]
    [Display(Name = "Affected account ID")]
    public string? TargetUserId { get => targetUserId; set => targetUserId = Clean(value); }

    [Range(1, int.MaxValue)]
    [Display(Name = "Schedule ID")]
    public int? ScheduleId { get; set; }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
