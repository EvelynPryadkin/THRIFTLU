using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LUThrift.Web.Services;

public sealed class StaffAccessService(
    ApplicationDbContext context,
    UserManager<ApplicationUser> users,
    ILookupNormalizer normalizer,
    TimeProvider timeProvider,
    ILogger<StaffAccessService> logger)
{
    public async Task<StaffAccessQueryResult> GetPageAsync(
        StaffAccessQueryInput? input, ClaimsPrincipal? user, int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        var actorId = CurrentAccountAccess.GetUserId(user);
        if (actorId is null)
            return new(StaffAccessQueryStatus.AuthenticationRequired, "Sign in to manage staff access.");
        var access = CurrentAccountAccess.AdministratorAccounts(context, actorId);
        if (!await access.AnyAsync(cancellationToken))
            return new(StaffAccessQueryStatus.AdministratorRequired, AdministratorMessage);

        var errors = new List<ValidationResult>();
        if (input is null || !Validator.TryValidateObject(input, new ValidationContext(input), errors, true))
            return new(StaffAccessQueryStatus.InvalidInput, errors.FirstOrDefault()?.ErrorMessage ?? "Enter a valid account search.");

        string? search = null;
        if (input.Search is not null)
        {
            try { search = normalizer.NormalizeEmail(input.Search); }
            catch (ArgumentException) { return InvalidSearch(); }
            if (string.IsNullOrEmpty(search) || search.Length > StaffAccessQueryInput.MaximumSearchLength)
                return InvalidSearch();
        }

        // Keep eligibility in the count and projection as well as the initial gate.
        var query = context.Users.AsNoTracking().Where(_ => access.Any());
        if (search is not null)
            query = query.Where(account => (account.NormalizedEmail != null && account.NormalizedEmail.Contains(search))
                || account.DisplayName.ToUpper().Contains(search));
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 1 : (totalCount - 1) / StaffAccessPage.PageSize + 1;
        var currentPage = Math.Clamp(pageNumber, 1, totalPages);
        var items = await query.OrderBy(account => account.NormalizedEmail).ThenBy(account => account.Id)
            .Skip((currentPage - 1) * StaffAccessPage.PageSize).Take(StaffAccessPage.PageSize)
            .Select(account => new StaffAccessAccount(account.Id, account.DisplayName, account.Email, account.IsActive,
                (from membership in context.UserRoles
                 join role in context.Roles on membership.RoleId equals role.Id
                 where membership.UserId == account.Id && role.Name == ApplicationRoles.Staff
                 select membership).Any(),
                (from membership in context.UserRoles
                 join role in context.Roles on membership.RoleId equals role.Id
                 where membership.UserId == account.Id && role.Name == ApplicationRoles.Administrator
                 select membership).Any()))
            .ToListAsync(cancellationToken);
        if (!await access.AnyAsync(cancellationToken))
            return new(StaffAccessQueryStatus.AdministratorRequired, AdministratorMessage);
        return new(StaffAccessQueryStatus.Success, "Accounts loaded.", new(items, totalCount, currentPage));
    }

    // The actor comes from authentication. The only submitted choices are an existing
    // account ID and the two supported actions; no submitted role name is ever used.
    public async Task<StaffAccessOperationResult> ChangeStaffAsync(
        string? targetUserId, StaffAccessAction action, ClaimsPrincipal? user,
        CancellationToken cancellationToken = default)
    {
        var actorId = CurrentAccountAccess.GetUserId(user);
        if (actorId is null)
            return new(StaffAccessOperationStatus.AuthenticationRequired, "Sign in to manage staff access.");
        try
        {
            if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
            if (string.IsNullOrWhiteSpace(targetUserId) || targetUserId.Length > 450
                || action is not (StaffAccessAction.Grant or StaffAccessAction.Remove))
                return new(StaffAccessOperationStatus.InvalidInput, "Choose an existing account and grant or remove Staff access.");
            if (context.Database.CurrentTransaction is not null) return Conflict();
            return await ChangeInTransactionAsync(targetUserId, action, actorId, cancellationToken);
        }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        catch (Exception exception) when (IsDatabaseConflict(exception)) { return Conflict(); }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            // Provider errors can contain account data. Do not log the exception or submitted fields.
            logger.LogError("Changing Staff access failed during persistence.");
            return PersistenceFailed();
        }
    }

    private async Task<StaffAccessOperationResult> ChangeInTransactionAsync(
        string targetId, StaffAccessAction action, string actorId, CancellationToken cancellationToken)
    {
        var postgres = context.Database.IsNpgsql();
        if (!postgres && context.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            throw new NotSupportedException("Staff access coordination supports PostgreSQL and SQLite.");
        await using var transaction = await context.Database.BeginTransactionAsync(
            postgres ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);

        // Serialize grants/removals for this target. Only the account row is locked;
        // this operation never acquires a listing lock or alters reservation lock order.
        var exists = await LockTargetAsync(targetId, postgres, cancellationToken);
        if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
        if (!exists) return new(StaffAccessOperationStatus.NotFound, "The account could not be found.");
        if (await HasRoleAsync(targetId, ApplicationRoles.Administrator, cancellationToken))
            return ProtectedAccount();
        var hasStaff = await HasRoleAsync(targetId, ApplicationRoles.Staff, cancellationToken);
        var grant = action == StaffAccessAction.Grant;
        if (hasStaff == grant)
            return new(StaffAccessOperationStatus.Unchanged, grant
                ? "This account already has Staff access. No change was made."
                : "This account does not have Staff access. No change was made.");

        var target = await users.FindByIdAsync(targetId);
        if (target is null) return new(StaffAccessOperationStatus.NotFound, "The account could not be found.");
        // UserManager's store shares this DbContext/transaction. Refresh any tracked
        // instance before Identity's concurrency-stamp checks, and avoid cached memberships.
        await context.Entry(target).ReloadAsync(cancellationToken);
        DetachMemberships(targetId);
        AdministrativeAuditEvent? audit = null;
        var committed = false;
        try
        {
            var changed = grant
                ? await users.AddToRoleAsync(target, ApplicationRoles.Staff)
                : await users.RemoveFromRoleAsync(target, ApplicationRoles.Staff);
            if (!changed.Succeeded) return IdentityFailure(changed);
            var refreshed = await users.UpdateSecurityStampAsync(target);
            if (!refreshed.Succeeded) return IdentityFailure(refreshed);
            if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
            if (await HasRoleAsync(targetId, ApplicationRoles.Administrator, cancellationToken)) return ProtectedAccount();

            audit = new AdministrativeAuditEvent
            {
                ActorUserId = actorId, TargetUserId = targetId,
                Action = grant ? AdministrativeAuditActions.StaffGranted : AdministrativeAuditActions.StaffRemoved,
                OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime
            };
            context.AdministrativeAuditEvents.Add(audit);
            await context.SaveChangesAsync(cancellationToken);
            if (!await IsAdministratorAsync(actorId, cancellationToken)) return AdministratorRequired();
            if (await HasRoleAsync(targetId, ApplicationRoles.Administrator, cancellationToken)) return ProtectedAccount();
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return new(StaffAccessOperationStatus.Changed, grant
                ? "Staff access granted. The account holder should sign out and back in."
                : "Staff access removed. The account holder should sign out and back in.");
        }
        finally
        {
            if (!committed)
            {
                if (audit is not null) context.Entry(audit).State = EntityState.Detached;
                context.Entry(target).State = EntityState.Detached;
                DetachMemberships(targetId);
            }
        }
    }

    private async Task<bool> LockTargetAsync(string targetId, bool postgres, CancellationToken cancellationToken)
    {
        if (postgres)
            return await context.Users.FromSqlInterpolated(
                    $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {targetId} FOR UPDATE")
                .AsNoTracking().Select(account => account.Id).SingleOrDefaultAsync(cancellationToken) is not null;
        // SQLite's transaction takes a writer lock through an unchanged-value update.
        return await context.Users.Where(account => account.Id == targetId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.ConcurrencyStamp,
                account => account.ConcurrencyStamp), cancellationToken) != 0;
    }

    private Task<bool> HasRoleAsync(string targetId, string roleName, CancellationToken cancellationToken)
        => (from membership in context.UserRoles.AsNoTracking()
            join role in context.Roles on membership.RoleId equals role.Id
            where membership.UserId == targetId && role.Name == roleName
            select membership).AnyAsync(cancellationToken);

    private Task<bool> IsAdministratorAsync(string actorId, CancellationToken cancellationToken)
        => CurrentAccountAccess.AdministratorAccounts(context, actorId).AnyAsync(cancellationToken);

    private void DetachMemberships(string targetId)
    {
        foreach (var entry in context.ChangeTracker.Entries<IdentityUserRole<string>>()
                     .Where(entry => entry.Entity.UserId == targetId).ToArray())
            entry.State = EntityState.Detached;
    }

    private static bool IsDatabaseConflict(Exception exception)
    {
        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        return postgres is not null && postgres.SqlState is PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable
            or PostgresErrorCodes.UniqueViolation;
    }

    private const string AdministratorMessage = "An active Administrator account is required.";
    private static StaffAccessQueryResult InvalidSearch()
        => new(StaffAccessQueryStatus.InvalidInput, "Enter a valid name or email search of 100 characters or fewer.");
    private static StaffAccessOperationResult AdministratorRequired()
        => new(StaffAccessOperationStatus.AdministratorRequired, AdministratorMessage);
    private static StaffAccessOperationResult ProtectedAccount()
        => new(StaffAccessOperationStatus.ProtectedAccount, "Administrator accounts cannot be changed here.");
    private static StaffAccessOperationResult Conflict()
        => new(StaffAccessOperationStatus.Conflict, "The account's access changed. Refresh and try again.");
    private static StaffAccessOperationResult PersistenceFailed()
        => new(StaffAccessOperationStatus.PersistenceFailed, "Staff access could not be saved. Refresh before trying again.");
    private static StaffAccessOperationResult IdentityFailure(IdentityResult result)
        => result.Errors.Any(error => error.Code == "ConcurrencyFailure") ? Conflict() : PersistenceFailed();
}
