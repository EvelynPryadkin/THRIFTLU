# Pickup schedule storage

Active Administrators manage the persisted schedule at **Admin → Pickup hours** (`/Admin/PickupHours`). Reservation creation, item availability hints, and campus-time displays now read the database schedule. Missing or empty hours disable new reservations without disabling public browsing. No restart is needed after a committed change.

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

The page displays the saved version and has labeled, bounded text fields for timezone, weekly rows (`Weekday, HH:mm, HH:mm`), and one closed date per line. Updates and imports are antiforgery-protected POSTs. The existing Administrator policy checks current account membership on GET and POST, and the service rechecks it for writes. Invalid edits remain on the form with errors; a stale version receives a refresh message rather than overwriting another edit. Successful saves redirect to the page.

## Schedule changes during requests

Each reservation operation loads one committed schedule after checking the student account and before expired-hold reconciliation and owner → listing locking. The full aggregate is read in one no-tracking query. All eligibility checks and any reconciliation retry use that captured schedule. Server time is still read afresh after lock waits and immediately before saving; a long wait cannot extend the pickup window.

If an Administrator changes hours after an operation has captured them, that already-running operation may finish against the previous schedule. A later operation captures the new version. A Reserve POST therefore reads again even when the item page was opened earlier; the browser's old availability message cannot authorize the POST.

Closing the schedule does not cancel, shorten, or extend existing reservations. Cancellation, expiration cleanup, pickup confirmation, and missing-item handling do not depend on opening hours. Their existing deadline and ownership rules remain authoritative. Reservation timestamps stay UTC; each response formats its dates using one captured persisted timezone and its applicable UTC offset. Changing that timezone changes the display, never the saved instant. Invalid legacy timezone data falls back to labeled UTC.

## Explicit import of existing configuration

There is no automatic startup import. The Administrator page offers a deliberate import only before a schedule has been saved, using `PickupScheduleService.ImportConfiguredAsync`. Configuration is an import source, not a runtime fallback.

1. Back up the intended database and apply `PersistPickupSchedules` with the normal `dotnet ef database update --project src/LUThrift.Web -- --environment Development` command. Confirm which database is configured before applying it. The migration creates no schedule records.
2. Review the effective `Reservations:CampusTimeZone`, `WeeklyPickupIntervals`, and `ClosedDates` from that environment. Confirm their source and approved hours; do not copy synthetic test hours into an operational schedule. Keep official hours unconfirmed until supplied.
3. Sign in as a currently active Administrator and open `/Admin/PickupHours`. Review the displayed configuration preview. Import is allowed only when no saved version exists; an intentionally empty saved schedule cannot be overwritten by import.
4. Choose the explicit import action. The protected POST obtains the source options on the server and passes the authenticated principal separately; no submitted identity or schedule settings choose the import source.
5. Require the page's saved confirmation before reporting success. Invalid options, lost Administrator access, a competing save, or audit failure leave the database unchanged. A repeated import returns `Conflict`, even if the saved schedule is empty. Do not delete the saved schedule to retry; use a versioned edit after reviewing it.
6. Review the redirected page's version, timezone, intervals, and closed dates. The service copies only those three settings and records `PickupScheduleImported` with the actor and server UTC time. Worker polling settings remain configuration-only. Subsequent reservation requests now use the imported hours.

If there are no confirmed configured hours to import, leave reservations unavailable until staff supplies them. Editing old configuration after import does not replace the database schedule.

Official hours, physical identity-check procedures, and cleanup-monitoring ownership remain unconfirmed.
