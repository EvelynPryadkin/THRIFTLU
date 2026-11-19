using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LUThrift.Web.Services;

public sealed class ActivityHistoryService(ApplicationDbContext context)
{
    public async Task<ActivityHistoryQueryResult> GetPageAsync(
        ActivityHistoryQueryInput? input, ClaimsPrincipal? user, int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        var actorId = CurrentAccountAccess.GetUserId(user);
        if (actorId is null)
            return new(ActivityHistoryQueryStatus.AuthenticationRequired, "Sign in to view activity history.");
        var access = CurrentAccountAccess.AdministratorAccounts(context, actorId);
        if (!await access.AnyAsync(cancellationToken)) return AdministratorRequired();

        var errors = new List<ValidationResult>();
        if (input is null || !Validator.TryValidateObject(input, new ValidationContext(input), errors, true))
            return Invalid(errors.FirstOrDefault()?.ErrorMessage ?? "Enter valid activity filters.");
        if (input.Action is not null && !ActivityHistoryPresentation.Actions(input.History).ContainsKey(input.Action))
            return Invalid("Choose an action from the selected history.");
        if (input.History == ActivityHistoryKind.Inventory && (input.TargetUserId is not null || input.ScheduleId is not null)
            || input.History == ActivityHistoryKind.Administration && input.ListingId is not null)
            return Invalid("Use filters belonging to the selected history.");

        IQueryable<Projection> records;
        // Access is checked in the count and data SQL as well as at entry and
        // before returning. A role claim or an earlier successful page is not enough.
        if (input.History == ActivityHistoryKind.Inventory)
        {
            var query = context.AuditEvents.AsNoTracking().Where(_ => access.Any());
            if (input.Action is not null) query = query.Where(row => row.Action == input.Action);
            if (input.ActorId is not null) query = query.Where(row => row.ActorUserId == input.ActorId);
            if (input.ListingId is not null) query = query.Where(row => row.ListingId == input.ListingId);
            records = query.Select(row => new Projection
            {
                Id = row.Id, Action = row.Action, ActorId = row.ActorUserId, OccurredAtUtc = row.OccurredAtUtc,
                ActorDisplayName = context.Users.Where(account => account.Id == row.ActorUserId)
                    .Select(account => account.DisplayName).FirstOrDefault(),
                ListingId = row.ListingId,
                ListingTitle = context.Listings.Where(listing => listing.Id == row.ListingId)
                    .Select(listing => listing.Title).FirstOrDefault()
                // Deliberately do not select Details or any image storage fields.
            });
        }
        else
        {
            var query = context.AdministrativeAuditEvents.AsNoTracking().Where(_ => access.Any());
            if (input.Action is not null) query = query.Where(row => row.Action == input.Action);
            if (input.ActorId is not null) query = query.Where(row => row.ActorUserId == input.ActorId);
            if (input.TargetUserId is not null) query = query.Where(row => row.TargetUserId == input.TargetUserId);
            if (input.ScheduleId is not null) query = query.Where(row => row.TargetScheduleId == input.ScheduleId);
            records = query.Select(row => new Projection
            {
                Id = row.Id, Action = row.Action, ActorId = row.ActorUserId, OccurredAtUtc = row.OccurredAtUtc,
                ActorDisplayName = context.Users.Where(account => account.Id == row.ActorUserId)
                    .Select(account => account.DisplayName).FirstOrDefault(),
                TargetUserId = row.TargetUserId, ScheduleId = row.TargetScheduleId,
                TargetDisplayName = context.Users.Where(account => account.Id == row.TargetUserId)
                    .Select(account => account.DisplayName).FirstOrDefault()
            });
        }

        var count = await records.CountAsync(cancellationToken);
        var totalPages = count == 0 ? 1 : (count - 1) / ActivityHistoryPage.PageSize + 1;
        var currentPage = Math.Clamp(pageNumber, 1, totalPages);
        var rows = await records.OrderByDescending(row => row.OccurredAtUtc).ThenByDescending(row => row.Id)
            .Skip((currentPage - 1) * ActivityHistoryPage.PageSize).Take(ActivityHistoryPage.PageSize)
            .ToListAsync(cancellationToken);
        if (!await access.AnyAsync(cancellationToken)) return AdministratorRequired();
        var items = rows.Select(row => new ActivityHistoryEntry(
            row.Id, ActivityHistoryPresentation.Label(input.History, row.Action),
            ActivityHistoryPresentation.Summary(input.History, row.Action), row.ActorId, row.ActorDisplayName,
            row.ListingId, row.ListingTitle, row.TargetUserId, row.TargetDisplayName, row.ScheduleId, row.OccurredAtUtc)).ToArray();
        return new(ActivityHistoryQueryStatus.Success, "Activity history loaded.", new(items, count, currentPage));
    }

    private static ActivityHistoryQueryResult AdministratorRequired()
        => new(ActivityHistoryQueryStatus.AdministratorRequired, "An active Administrator account is required.");
    private static ActivityHistoryQueryResult Invalid(string message)
        => new(ActivityHistoryQueryStatus.InvalidInput, message);

    private sealed class Projection
    {
        public int Id { get; init; }
        public string Action { get; init; } = string.Empty;
        public string ActorId { get; init; } = string.Empty;
        public string? ActorDisplayName { get; init; }
        public int? ListingId { get; init; }
        public string? ListingTitle { get; init; }
        public string? TargetUserId { get; init; }
        public string? TargetDisplayName { get; init; }
        public int? ScheduleId { get; init; }
        public DateTime OccurredAtUtc { get; init; }
    }
}
