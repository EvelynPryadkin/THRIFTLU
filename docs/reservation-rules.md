# Reservation Rules

These rules describe the reservation policy for the LU Thrift Room. Official pickup hours and operating procedures still need confirmation from thrift-room staff. Week 6 implements policy, pickup-hours eligibility, student creation, owner cancellation, background expiration, and My Reservations; checkpoint 6 verifies that workflow. Students reserve from public item details through a protected POST to `/Account/Reserve`, then view and cancel their own reservations at `/Account/Reservations`. Week 7 adds the staff pickup dashboard and atomic pickup confirmation. Reporting a reserved item missing remains future work.

## Reservation Duration

* A reservation lasts for **one hour**.
* The one-hour period begins when the system successfully confirms the reservation.
* The student will be shown the exact time by which the item must be collected.
* The reservation is no longer valid once the deadline has been reached.
* One hour means 60 minutes of elapsed time, even when daylight saving time changes the local clock. The server supplies the start and deadline; the browser cannot choose them.

## Reservation Limit

* A student may have up to **three active reservations** at one time.
* Completed, canceled, and expired reservations do not count toward this limit.
* If a student already has three active reservations, the system will not allow another reservation until one is completed, canceled, or expired.
* A reservation counts as active only when `Status == Active` **and** `ExpiresAtUtc > current server time`. At `ExpiresAtUtc == current server time`, it is expired and does not count, even if a later cleanup process has not yet updated its stored status.
* The one-hour duration and maximum of three are fixed in `ReservationPolicy`; they are not schedule configuration settings or form fields.

## Pickup Hours

* Students may only reserve items during the thrift room’s established pickup hours.
* A reservation will only be accepted if the complete one-hour reservation period fits within one continuous open pickup interval.
* Opening time is included. Starting exactly one hour before closing is allowed; starting even one tick later is not. The deadline may equal closing time, but the reservation is no longer active at that deadline.
* A reservation cannot start during a break or span a break. Separate weekly intervals on the same day describe the open periods on either side of a break.
* A closed date overrides the weekly schedule. Weekdays and closed dates are interpreted in the campus timezone, not the server machine's timezone or the student's device timezone.
* When hours are missing or invalid, new reservations remain unavailable. Public browsing continues to work.

## Configuration and Time Handling

The `Reservations` configuration section ships without pickup hours because none have been confirmed:

```json
{
  "Reservations": {
    "CampusTimeZone": "America/Chicago",
    "WeeklyPickupIntervals": [],
    "ClosedDates": [],
    "ExpirationPollSeconds": 30,
    "ExpirationBatchSize": 50
  }
}
```

Once staff confirm the schedule, each `WeeklyPickupIntervals` entry needs:

| Field | Meaning |
| --- | --- |
| `Day` | An English weekday name, such as `Monday`; case does not matter. |
| `OpensAt` | Campus-local opening time in 24-hour `HH:mm` format. |
| `ClosesAt` | Campus-local closing time in 24-hour `HH:mm` format, later on the same day. |

`ClosedDates` contains campus-local dates in `yyyy-MM-dd` format. There is no schedule-management screen. Configure the confirmed values through the normal ASP.NET Core configuration sources and restart the application after changes. The tests use synthetic hours; those are not an official schedule.

Intervals must stay within one calendar day. Overlapping, duplicate, or touching intervals are invalid; write a continuous open span as one interval instead. Invalid weekdays, times, dates, or timezone IDs make the schedule unavailable rather than guessing a correction. Blank or incomplete intervals are invalid too. Overnight hours and date-specific replacement hours are outside this checkpoint.

`PickupHoursEligibility` reads one instant from injected `TimeProvider`, converts it to the configured campus timezone to select the date and weekday, and converts that day's open-interval endpoints back to UTC. It adds one elapsed hour to the captured UTC instant and checks that both start and deadline fit the same interval. Eligible results contain UTC start and expiration values; ineligible results contain a reason and no reservation window. This is a read-only eligibility calculation, not a booking or a promise that an item remains available. `ReservationService` rechecks eligibility, the student's active count, and the listing inside its creation transaction.

If a configured endpoint falls in a daylight-saving gap or repeated hour, that day's schedule is unavailable: the helper does not guess which instant staff intended. When endpoints are unambiguous, an interval may include a clock change; duration is still measured in UTC. Normal winter and summer dates therefore use the campus timezone's applicable offset rather than a hard-coded offset. All persisted reservation timestamps remain UTC in the existing database columns.

## Creation and Database Coordination

`CreateReservationInput` contains only `ListingId`. The protected Reserve PageModel binds it from form data and passes its authenticated `User` separately to `ReservationService.CreateAsync`; it never constructs identity from form fields. The service reads the user ID from that principal and verifies an existing, active account with current Student membership in the database. Role claims alone are insufficient. Creation times, the deadline, status, and audit fields come from the server.

The public details GET shows a sign-in link or a Reserve form when the account, item, and pickup-hours hints permit it. Closed or unconfigured pickup hours are explained. The form posts to the Student-protected `/Account/Reserve` page with normal Razor Pages antiforgery protection. The anonymous Thrift folder has no reservation mutation handler. POST rechecks every rule, so hidden buttons, stale forms, and forged extra fields cannot grant a reservation. Success redirects to My reservations and displays the committed deadline in campus time with a timezone and UTC-offset label. Existing saved timestamps fall back to labeled UTC if the timezone configuration later becomes invalid. All GET requests remain read-only for inventory, reservations, and audits.

PostgreSQL creation uses one `ReadCommitted` transaction and a consistent lock order:

1. Lock the student's `AspNetUsers` row with `SELECT ... FOR UPDATE`.
2. Lock the requested listing row with `SELECT ... FOR UPDATE`.
3. Check Student membership, pickup eligibility with fresh server time, listing availability, existing stored Active reservations for that listing, and the student's active count.
4. Conditionally change Available to Held. Recheck pickup eligibility immediately before saving; if the clock moved backward, return a retry result instead of trusting a count from a later instant.
5. Save the Active reservation and `ListingReserved` audit, then commit. The audit contains the listing ID, authenticated actor ID, UTC time, and fixed transition details. Any failed write or final eligibility check rolls back the entire operation.

The user lock serializes competing requests for different items by the same student. The listing lock coordinates different students requesting one item. Locks last until commit or rollback, so the next waiting transaction sees committed changes before checking its limit or the listing. Every future reservation writer must preserve this coordination and lock order. Staff lifecycle actions already use conditional listing updates and cannot change a Held listing.

The `EnforceSingleActiveReservationPerListing` migration adds a unique listing index filtered by the actual stored string `"Status" = 'Active'`. It rejects two stored Active rows even if one deadline has passed. Its preflight reports conflicting listing IDs and leaves existing data/history intact; resolve conflicts explicitly before retrying the migration. It does not silently cancel or delete records.

An expired stored Active row immediately stops counting against its student's limit. Before creation, the service checks the requesting account's eligibility, then reconciles an expired hold for the requested listing in a separate transaction: lock the old owner, lock the listing, reread the reservation and current time, then expire/release/audit together. Only after that transaction ends does creation take the new student's locks. This avoids acquiring an old owner's lock while already holding the new student and listing locks. If the hold crosses its deadline while creation waits, one bounded reconciliation/retry is allowed. A competing student may still win the item; creation rechecks availability rather than assuming cleanup reserved it.

Cleanup can commit even if the subsequent creation is declined, since an expired reservation must stay expired regardless of new pickup eligibility. Duplicate submissions return AlreadyReserved without extending the original deadline. Database uniqueness, serialization, deadlock, and lock-unavailable conflicts return a refresh/retry result; other persistence failures return a safe message without database details. There is no automatic retry after an uncertain commit outcome.

SQLite tests use serializable write transactions, with unchanged-value updates to coordinate writers. They verify service decisions and rollback, but PostgreSQL row-lock behavior is verified separately against a disposable database. No process-local lock is used.

## Cancellation, Expiration, and Release

* An existing active Student account may cancel its own active reservation before the deadline, even outside pickup hours or when new reservations are disabled. The submitted form supplies only the reservation ID; ownership comes from the authenticated server identity and database. Unknown and other-owner IDs produce the same safe result.
* Cancellation changes the stored status to `Canceled`, removes it from the active count, and releases its hold. At or after the deadline, cancellation instead records `Expired`. Both decisions use fresh server time after waiting for locks. Students cannot cancel another student's reservation.
* Releasing a hold changes `Held` to `Available` only when there is no other stored Active reservation and no newer reservation history for that listing, ordered by creation time and then ID. Orphan holds and ambiguous newer history remain protected. No operation turns Missing, Collected, Archived, or any other non-Held state into Available.
* Cancellation and expiration each lock owner → listing, reread the reservation, conditionally transition it, release only its eligible hold, and save one audit in a transaction. Failed writes roll everything back. Repeated or competing processing sees the completed status and performs no further transition, release, or audit. All reservation history is retained.
* Cancellation audits use the student's ID. Expiration audits use the historical scalar actor `system:reservation-expiration`; no synthetic login account is created. Safe details identify the reservation and whether its hold was released or inventory stayed unchanged.
* Staff pickup confirmation verifies the current hold and changes the reservation to `Fulfilled` and listing to `Collected`, saving the server UTC fulfillment time and staff audit in the same transaction. Student membership alone never grants pickup-confirmation permission. The physical identity-check procedure and any late-arrival exceptions still need staff agreement; the implemented deadline remains strict.

## Immediate Deadlines and Background Cleanup

The deadline determines validity; the worker does not. `Status == Active && ExpiresAtUtc > now` remains the active-count rule even if the app was stopped or cleanup is delayed. Account GETs project overdue Active rows as Expired and hide their cancellation controls. Catalog GETs project a known expired hold as Available only when the same release-ownership checks above succeed. That effective status participates in SQL filters, counts, paging, and details. GETs never save these projections, and reservation ownership is never exposed publicly. A page already open in the browser is a snapshot until refreshed; server-side actions always recheck time.

`ExpireReservationsWorker` runs an immediate pass at host startup and then polls. Each pass creates and asynchronously disposes a service scope, selecting at most `ExpirationBatchSize` expired Active IDs ordered by deadline then ID. Each selected transition has its own atomic transaction. The default is 50 records every 30 seconds after the previous pass; batch size is bounded to 1–200 and interval to 1–300 seconds. Failures are logged without database details and retried in later passes. Shutdown cancels both database work and the delay. Multiple hosts may run the worker safely because transitions share database locks and conditional updates.

The worker does not consult pickup hours, Student membership, or account active state. Expiration continues when creation is disabled. During application downtime no worker runs, but deadlines remain unchanged; startup cleanup and creation-time reconciliation catch up. There is no independent external scheduler or staff repair screen in this checkpoint.

## My Reservations and Ownership

My Reservations shows all current, unexpired Active reservations and the active count out of three. A separate recent-history section shows up to 20 Canceled, Expired, Fulfilled, or overdue stored Active records. History is ordered by creation time descending, then reservation ID descending for stable ties; the history limit never removes current reservations from the display or count. Older history is preserved in the database.

The owner comes only from the signed-in server identity. Both query branches apply that owner filter before projection or limiting, and one combined SQL query uses the same captured UTC instant for the count and sections. Passing another `UserId` or reservation ID to the page does not choose another person's history. Cancellation checks ownership and status again under the existing locks; a valid login or Student role alone does not authorize access to somebody else's reservation.

Titles and reservation dates remain visible for owned historical records even after the listing leaves the public catalog. Fulfilled records also show their saved `FulfilledAtUtc` as the collection time; legacy records without that timestamp say it was not recorded. The page shows campus-local times and an explicit timezone/offset, without linking private history to unavailable public detail routes. No GET updates the reservation status, releases a listing, or creates an audit; the deadline still controls effective status and count until a later POST or worker persists cleanup.

## Staff Pickup Queries (Week 7, Checkpoint 1)

The staff PageModel passes its authenticated server principal and validated search input to `StaffPickupService.GetPageAsync`, which queries EF Core through `ApplicationDbContext`. The existing Staff-or-Administrator folder policy remains unchanged. The query service additionally checks that the account still exists, is active, and currently has Staff or Administrator membership in the database. Cookie role claims alone cannot grant access to student information. This access check runs before any reservation query, is included in the count and row SQL, and is checked again before returning the result. Anonymous, inactive, missing, or no-longer-authorized accounts receive an access result without a page of student data.

With no reservation ID, the query returns only stored Active reservations with `ExpiresAtUtc >` one captured server UTC time. Student-email search trims surrounding whitespace, treats a blank value as no filter, and accepts up to 256 characters. It uses Identity's registered email normalizer and literal substring matching against `NormalizedEmail`; case differences are ignored according to that normalization, while `%`, `_`, and `!` have no wildcard meaning. Control characters and invalid Unicode are rejected. This is an email search, so it intentionally accepts partial addresses rather than requiring a complete address. No accounts or emails are modified by searching.

An exact positive reservation ID can retrieve an Active, Fulfilled, Canceled, or Expired record for explanation. An overdue stored Active row is displayed as Expired immediately, without saving cleanup. A record whose deadline equals the captured server time is expired. When ID and email are supplied together, both must match; an unknown or mismatched lookup returns an empty successful page. Invalid input returns an explicit validation result instead of silently broadening the search. The PageModel also rejects binding errors for malformed numeric URL values without loading pickup records.

Each row contains the reservation ID, item title, student display name/email, effective reservation status, stored listing status, UTC deadline, and a `CanConfirmPickup` display flag. The flag checks the deadline, Held state, consistent timestamps/metadata, and absence of a competing or newer reservation; it is only a snapshot for showing the form. The POST repeats the rules under locks. The query does not return an account ID, account entity, audit records, cancellation details, password information, or tokens. Email can be absent on a legacy account. Inconsistent records remain visible for staff diagnosis without a confirmation button. Completed or expired ID lookups are history, not collectible reservations.

Results are counted after filtering and ordered by deadline ascending, then reservation ID ascending, so the earliest deadlines appear first and ties are stable. Page size is fixed on the server at 20. Empty results use page 1; page numbers below 1 or beyond the last page are clamped before calculating the offset. Ordering, `Skip`, `Take`, and projection run in the database. Queries are asynchronous, use cancellation tokens and no tracking, and never change inventory, reservations, timestamps, or audits. Pickup display and confirmation reuse the existing schema.

## Staff Pickup Confirmation and Remaining Transition

Pickup confirmation is implemented; reporting a reserved item missing is planned for a later checkpoint:

| Staff action | Required reservation and listing state | Reservation after success | Listing after success |
| --- | --- | --- | --- |
| Confirm a valid pickup | Active, `ExpiresAtUtc >` fresh server time, and its listing still Held for this reservation | Fulfilled; set the existing `FulfilledAtUtc` to server UTC time | Collected |
| Report the reserved item missing | Active, unexpired, and its listing still Held for this reservation | Canceled; record a safe server-supplied missing-item reason | Missing |

The Confirm pickup form sits beside the item, student, reservation ID, and deadline at `/Staff/Pickups`. Its antiforgery-protected POST submits only the reservation ID; the PageModel takes the staff identity from authentication and calls `ReservationService.ConfirmPickupAsync`. Submitted owner IDs, staff IDs, statuses, timestamps, and query-string reservation IDs cannot choose or authorize the update. The service requires a currently active Staff or Administrator account and locks the student reservation owner, then the listing. It rereads the records and checks fresh server time after waiting and immediately before the transition. An existing unexpired hold can be collected near closing without passing creation's full-hour availability check.

The reservation, listing change, and audit commit atomically. The audit identifies the staff actor, reservation, and Active/Fulfilled and Held/Collected transitions. A failed write rolls them all back. The page redirects after POST and displays success only after the service commits; refreshing the resulting GET cannot collect again. Stale, unknown, expired, and already-completed submissions receive safe feedback. Repeated requests preserve the original fulfillment time and audit. The collected item disappears from every public catalog filter and its public details return 404, while the student's own history retains the item and collection time.

The future missing-item action must use the same authorization, deadline, and reservation-owner → listing coordination, and must leave the item Missing rather than Available. Neither action may overwrite a newer hold, delete history, or trust a status, actor, or timestamp submitted by the browser. An earlier query result is only a snapshot and cannot authorize a later write.

The strict deadline remains authoritative: at or after `ExpiresAtUtc`, the reservation is expired and cannot be fulfilled, extended, or treated as an active pickup. Expired and completed records can be looked up for explanation; any subsequent inventory handling must respect the current listing and reservation state. Official identity-check procedures and late-arrival exceptions are **unconfirmed**. No grace period or identity-document requirement is being invented here. Official pickup hours also remain unconfirmed.

## Verification and Remaining Checks

On October 27, 2026, the complete suite passed **514 tests, 0 failed, 0 skipped**, including 19 reservation and 3 catalog tests against dedicated disposable PostgreSQL 18.6 databases. Restore and build passed with no warnings or errors; the safe EF model check found no pending changes and the whitespace check passed. The development database was not used. No migration or production-code change was needed in checkpoint 6.

PostgreSQL race tests use committed fixtures, separate connections/DbContexts, a shared start signal, and observed lock waits before releasing competing operations. They verify one winner for a shared item, at most one success when a student with two holds requests two more items, one hold/audit for simultaneous duplicates, and cancellation/expiration racing a replacement without releasing its new hold. The actual migration and filtered Active index are exercised, including refusal of conflicting preexisting data without deleting it. Failed audit writes roll back reservation and inventory changes.

The HTTP workflow test creates three reservations through protected forms, rejects a fourth, cancels to free a slot, then crosses the exact deadline and reserves again while other overdue rows still await cleanup. Account and public GETs reflect the deadline without saving changes. Existing controlled-time tests cover pickup boundaries, daylight saving, repeated processing, ownership, and worker failures/shutdown; existing Staff and catalog tests still pass.

The Week 6 Chrome browser walkthrough was completed on October 28; its actual observations and coverage limits are recorded in the README. Official pickup hours remain unconfirmed, and the shipped empty schedule keeps creation unavailable. The reserved-item missing action and the operational decisions below remain open.

## Decisions Still Needed

* Confirm official weekly pickup days, opening and closing times, and breaks.
* Confirm closure dates, holidays, and who maintains configuration changes.
* Confirm `America/Chicago` as the campus timezone and the procedure for any hours near a daylight-saving change.
* Decide whether overnight openings or one-off replacement hours will ever be needed.
* Agree on pickup identity checks, staff confirmation steps, and late-arrival handling.
* Confirm the operational polling interval, monitoring of failed cleanup passes, and how staff will handle orphan holds or items that cannot be found. The exact-deadline policy holds regardless of cleanup timing.
