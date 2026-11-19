using System.ComponentModel.DataAnnotations;
using LUThrift.Web.Configuration;
using LUThrift.Web.InputModels;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace LUThrift.Web.Pages.Admin.PickupHours;

// The /Admin folder policy checks current active Administrator membership for
// every request; the schedule service independently checks it for each write.
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
[RequestSizeLimit(32 * 1024)]
[RequestFormLimits(ValueLengthLimit = 8192, ValueCountLimit = 16)]
public class IndexModel(PickupScheduleService schedules, IOptions<ReservationOptions> configured) : PageModel
{
    [BindProperty]
    public PickupHoursForm Input { get; set; } = new();

    public PickupScheduleSnapshot Saved { get; private set; } = new(null, "America/Chicago", [], []);
    public string ConfiguredTimeZone => configured.Value.CampusTimeZone;
    public string ConfiguredIntervals => IntervalsText(configured.Value.WeeklyPickupIntervals ?? []);
    public string ConfiguredClosedDates => string.Join(Environment.NewLine, configured.Value.ClosedDates ?? []);
    public bool HasConflict { get; private set; }

    [TempData]
    public string? PickupHoursMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Saved = await schedules.GetAsync(cancellationToken);
        Input = new()
        {
            ExpectedVersion = Saved.Version,
            CampusTimeZone = Saved.CampusTimeZone,
            WeeklyIntervalsText = IntervalsText(Saved.WeeklyPickupIntervals),
            ClosedDatesText = string.Join(Environment.NewLine, Saved.ClosedDates)
        };
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var update = ModelState.IsValid ? ParseInput() : null;
        if (!ModelState.IsValid)
        {
            Saved = await schedules.GetAsync(cancellationToken);
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }

        var result = await schedules.SaveAsync(update, User, cancellationToken);
        if (result.Status == PickupScheduleOperationStatus.AuthenticationRequired) return Challenge();
        if (result.Status == PickupScheduleOperationStatus.AdministratorRequired) return Forbid();
        if (result.Succeeded)
        {
            PickupHoursMessage = result.Message;
            return RedirectToPage("./Index");
        }

        Saved = await schedules.GetAsync(cancellationToken);
        ModelState.AddModelError(string.Empty, result.Message);
        HasConflict = result.Status == PickupScheduleOperationStatus.Conflict;
        Response.StatusCode = HasConflict ? StatusCodes.Status409Conflict
            : result.Status == PickupScheduleOperationStatus.InvalidInput ? StatusCodes.Status400BadRequest
            : StatusCodes.Status503ServiceUnavailable;
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        // Import copies reviewed server configuration only. No submitted hours,
        // version, identity, duration, or reservation limit is used by this handler.
        var result = await schedules.ImportConfiguredAsync(configured.Value, User, cancellationToken);
        if (result.Status == PickupScheduleOperationStatus.AuthenticationRequired) return Challenge();
        if (result.Status == PickupScheduleOperationStatus.AdministratorRequired) return Forbid();
        PickupHoursMessage = result.Message;
        return RedirectToPage("./Index");
    }

    private UpdatePickupScheduleInput? ParseInput()
    {
        var intervalLines = Lines(Input.WeeklyIntervalsText);
        var closedLines = Lines(Input.ClosedDatesText);
        if (intervalLines.Length > PickupScheduleValidator.MaximumWeeklyIntervals)
            ModelState.AddModelError("Input.WeeklyIntervalsText", "Use at most 42 opening intervals.");
        if (closedLines.Length > PickupScheduleValidator.MaximumClosedDates)
            ModelState.AddModelError("Input.ClosedDatesText", "Use at most 366 closed dates.");
        if (!ModelState.IsValid) return null;

        var intervals = new List<WeeklyPickupInterval>();
        foreach (var line in intervalLines)
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 3)
            {
                ModelState.AddModelError("Input.WeeklyIntervalsText",
                    "Use one interval per line: weekday name, opening time, closing time. Separate the three values with commas.");
                return null;
            }
            intervals.Add(new() { Day = fields[0], OpensAt = fields[1], ClosesAt = fields[2] });
        }
        return new()
        {
            ExpectedVersion = Input.ExpectedVersion, CampusTimeZone = Input.CampusTimeZone,
            WeeklyPickupIntervals = intervals, ClosedDates = closedLines.ToList()
        };
    }

    private static string[] Lines(string? value) => (value ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static string IntervalsText(IEnumerable<WeeklyPickupInterval> intervals)
        => string.Join(Environment.NewLine, intervals.Select(interval =>
            interval is null ? "Invalid configured interval" : $"{interval.Day}, {interval.OpensAt}, {interval.ClosesAt}"));

    public sealed class PickupHoursForm
    {
        public const int MaximumIntervalsTextLength = 2048;
        // Browser textareas submit CRLF: 366 date rows can exceed 4 KiB.
        public const int MaximumClosedDatesTextLength = 5000;
        public Guid? ExpectedVersion { get; set; }

        [Required, StringLength(PickupScheduleValidator.MaximumTimeZoneLength)]
        [Display(Name = "Campus timezone")]
        public string CampusTimeZone { get; set; } = "America/Chicago";

        [StringLength(MaximumIntervalsTextLength)]
        [Display(Name = "Weekly opening intervals")]
        public string? WeeklyIntervalsText { get; set; }

        [StringLength(MaximumClosedDatesTextLength)]
        [Display(Name = "Closed dates")]
        public string? ClosedDatesText { get; set; }
    }
}
