# Pickup schedule storage

This checkpoint provides storage and services. Reservation creation still reads `Reservations` configuration through the existing pickup-hours helper. The Administrator UI and reservation integration come in Prompt 4; saving or importing a schedule does not change the live hours yet.

`PersistPickupSchedules` adds one schedule aggregate, its weekly opening intervals, and its closed dates. It preserves existing accounts, inventory, reservations, migrations, and audits. It does not seed hours. Administrative audits can now target either an account or the schedule, never both. Historical identifiers have no cascading relationship to their targets.

Downgrading this migration is blocked when schedule audits exist: the previous schema cannot retain their schedule targets. Plan any rollback with a backup and retained history; do not replace a schedule target with a fabricated account ID.

## Rules

- The schedule stores a recognized campus timezone (default `America/Chicago`, at most 100 characters). Weekly times and closed dates describe local campus time; reservation timestamps remain UTC.
- At most 42 weekly intervals and 366 submitted closed dates are accepted. Weekdays use names, times use `HH:mm`, and dates use `yyyy-MM-dd`.
- Opening must precede closing on the same day. Overlapping, duplicate, or touching intervals are invalid. Combine continuous hours into one interval; separate intervals describe breaks. Duplicate closed dates are normalized to one date.
- Closed dates override weekly hours. Missing or empty hours are unconfigured and cannot enable new reservations. Invalid input is rejected without changing the saved schedule.
- The shared eligibility helper requires the full elapsed hour inside one interval. It rejects ambiguous or nonexistent daylight-saving endpoints on the affected date. Existing deadline rules remain unchanged.
- Duration is always one hour and the active limit is always three. Neither is a schedule field.

## Read and save

`GetAsync` returns a no-tracking snapshot and version. A missing schedule returns empty collections and a null version without creating records. An intentionally saved empty schedule has a version and remains unconfigured.

`SaveAsync` accepts schedule fields and the version that the editor originally read. The caller passes the authenticated principal separately; the service checks current, active Administrator membership in the database. Existing schedule updates compare the version in SQL before replacing child records. A singleton primary key protects competing first saves. Stale edits return `Conflict`: reload and review the winning version rather than automatically resubmitting.

The schedule, new version, and administrative audit commit together. Failed writes leave the previous schedule and version intact. A normalized, unchanged save with the current version makes no audit. Audit identity and UTC time come from the server. Reads, startup, and imports never alter the configured reservation duration or limit.

## Explicit import of existing configuration

There is deliberately no startup import, browser route, or command-line importer in this checkpoint. The import entry point is `PickupScheduleService.ImportConfiguredAsync`; the following procedure is for a trusted administrative caller with an already authenticated principal. Prompt 4 can expose this deliberate operation through its protected interface.

1. Back up the intended database and apply `PersistPickupSchedules` with the normal `dotnet ef database update --project src/LUThrift.Web -- --environment Development` command. Confirm which database is configured before applying it. The migration creates no schedule records.
2. Review the effective `Reservations:CampusTimeZone`, `WeeklyPickupIntervals`, and `ClosedDates` from that environment. Confirm their source and approved hours; do not copy synthetic test hours into an operational schedule. Keep official hours unconfirmed until supplied.
3. Resolve the scoped service and `IOptions<ReservationOptions>` from the same environment. Call `GetAsync` first. Import is allowed only if `Version` is null; an intentionally empty saved schedule must not be overwritten by import.
4. After the Administrator explicitly chooses import, pass those reviewed options and the trusted authenticated principal to the service:

   ```csharp
   // Within an authenticated administrative caller's dependency-injection scope.
   // authenticatedUser comes from server authentication, never a submitted ID.
   var service = services.GetRequiredService<PickupScheduleService>();
   var configured = services.GetRequiredService<IOptions<ReservationOptions>>().Value;
   var result = await service.ImportConfiguredAsync(configured, authenticatedUser, cancellationToken);
   ```

5. Require `result.Status == PickupScheduleOperationStatus.Saved` before reporting success. Invalid options, lost Administrator access, a competing save, or audit failure leave the database unchanged. A repeated import returns `Conflict`, even if the saved schedule is empty. Do not delete the saved schedule to retry; use a versioned edit after reviewing it.
6. Read the saved snapshot back and compare timezone, intervals, and closed dates with the reviewed configuration. The service copies only those three settings and records `PickupScheduleImported` with the actor and server UTC time. Worker polling settings remain configuration-only.

Keep the existing runtime configuration in place for this checkpoint. Switching reservation eligibility and campus-time display to the persisted schedule is a separate, explicit Prompt 4 change. No existing reservation deadline is rewritten by storage or import.

Official hours, physical identity-check procedures, and cleanup-monitoring ownership remain unconfirmed.
