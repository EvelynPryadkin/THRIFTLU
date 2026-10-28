# LU Thrift

LU Thrift is a senior capstone project for the Lawrence University thrift room in Colman Hall. Students currently have to visit and search the room to see what's available. The goal is to let them browse online and reserve free items before heading over.

## Planned first version

- Students can browse, search, filter, view item details, and manage their own reservations.
- Staff can add, edit, publish, archive, or mark items missing, and confirm pickups.
- Administrators can manage staff access and operating settings.

Reservations last one hour, with a limit of three active reservations per student. The full hour has to fit within pickup hours. See the [reservation rules](docs/reservation-rules.md) for the details.

The first release covers official thrift-room inventory only. Student Exchange might come later. There are no payments, auctions, ratings, or real-time chat.

## Stack

ASP.NET Core Razor Pages and C# on .NET 10, with Entity Framework Core, PostgreSQL, and xUnit.

## Status

The Week 3 account and authorization foundation, Week 4 staff inventory workflow, and Week 5 public catalog are implemented on top of PostgreSQL. The home page is a landing page with a Browse items link to `/Thrift`.

Registration collects a display name, email, password, and password confirmation. Email addresses must be unique, and display names are required with a 100-character limit. The server creates the account and assigns Student in one transaction before signing the user in. The form has no role field, and submitted role values are ignored. Login uses a generic error for invalid credentials, and logout requires an antiforgery-protected POST.

Student, Staff, and Administrator roles are created at startup. Staff pages require Staff or Administrator; Admin pages require Administrator. The optional development administrator setup below promotes only an existing account. Email confirmation is disabled while there is no email service.

Staff and Administrators can open `/Staff/Inventory` to view every listing status, filter by status, and see the newest listings first. The dashboard links to Create and Edit. New listings always start as Draft; editing changes only the title, description, condition, optional size, and category. Draft, Available, and Missing listings can be edited. The server validates required fields, length limits, and category existence through `ListingService`.

The lifecycle actions are:

| Action | From | To |
| --- | --- | --- |
| Publish | Draft | Available |
| Mark missing | Available | Missing |
| Record walk-in collection | Available | Collected |
| Archive | Draft, Available, Missing | Archived |

Each action uses an antiforgery-protected POST. The service checks the current database status, even if someone changes submitted fields or sends a request directly. The actor ID comes from the signed-in user. Creating, editing, and changing a listing's status each record an audit event; the listing change and audit are saved together in a transaction. Staff archive records instead of deleting them. Audit history prevents deletion of its listing and keeps the actor's historical ID independently of the current account.

Real image upload is not implemented; inventory and catalog pages use a local SVG placeholder until a later checkpoint. Week 6 includes student reservation creation, owner cancellation, and background expiration. Staff pickup confirmation remains unfinished. Held belongs to that reservation workflow and cannot be edited or changed by the Week 4 staff actions. Collected and Archived are terminal for the staff workflow. There is no audit-history screen or staff-role management screen yet. Pickup hours, room procedures, campus sign-in, hosting, and maintenance are **To Be Confirmed**.

### Public catalog

Signed-out visitors and signed-in users can browse `/Thrift` and open `/Thrift/Details/{id}`. Cards show the title, category, condition, optional size, availability, and placeholder image. Details also show the description. Missing sizes say **Not specified**, and listing text uses Razor's normal HTML encoding. Details show Held as **Reserved**, without identifying who reserved it. Unknown IDs and Draft, Missing, Collected, or Archived details return HTTP 404.

The GET filter form combines case-insensitive title substring search, category, size, and availability. **Available** is the default; **Reserved** means Held; **All catalog items** means Available plus Held only. Categories come from the database, and size choices come from publicly visible listings. Search text is trimmed and treats `%`, `_`, and `!` literally. PostgreSQL uses `ILIKE`; SQLite verification alone is not treated as proof of PostgreSQL search behavior.

Search and size are limited to 150 and 50 characters. Malformed filter values show validation errors with HTTP 400; valid but unmatched choices show a no-results message. Filter selections stay visible and are stored in the URL. Clear filters restores the default catalog.

Pages contain at most 12 items, with the filtered total and current page shown above the cards. Database ordering is newest `CreatedAtUtc` first, then highest `Id` to resolve ties. The `pageNumber` parameter selects a page; missing, malformed, nonpositive, or outside-32-bit-integer values use page 1. Valid numbers beyond the last page use the last page. Empty results show page 1 of 1. Page size is fixed on the server.

Previous/Next links preserve the filters, submitting new filters resets to page 1, and an item's Back to catalog link restores its filters and page. Ordering is stable for unchanged results; staff updates between requests can shift items between pages.

The browsing flow is browser GET → Razor PageModel → `CatalogService` → EF Core → PostgreSQL. The service applies public visibility and all filters before counting or fetching a page with `Skip` and `Take`. It projects only display fields with no tracking. Browsing does not save listings, reservations, or audit events. The details page's separate Reserve form submits a protected POST described below.

### Reservations (Week 6)

Signed-out visitors can browse and open details, which offer a sign-in link returning to that public item GET. An active Student sees **Reserve item** for an Available item when pickup eligibility permits a full hour. Closed, unconfigured, and insufficient pickup time each have an explanation. The display is a hint; it does not promise availability or bypass the reservation limit.

The form posts only `ListingId` and its antiforgery token to `/Account/Reserve`. The entire receiving page requires Student authorization under the protected Account folder; no creation handler lives under the anonymous Thrift folder. `ReservationService.CreateAsync` receives the authenticated server principal and rechecks the persisted account's active state and Student membership, configured pickup hours, Available status, and the three-active-reservation limit. It saves a one-hour Active reservation, the Held listing status, and a `ListingReserved` audit together. Submitted owner IDs, roles, statuses, durations, or timestamps are ignored because they are absent from the input model.

Business outcomes redirect to My reservations with a useful message, including stale availability, duplicate submissions, limits, closed hours, and conflicts. Successful confirmation displays the deadline returned by the committed service operation. It never adds an hour to the browser's clock or to a later GET. A GET to `/Account/Reserve` only redirects to browsing and cannot replay a reservation.

PostgreSQL transactions lock the student account first, then the listing, so both same-student and same-item races are coordinated across application instances. Time is read after lock waits and checked again before saving. A filtered unique index also prevents multiple stored Active reservations for one listing. Before creation, an expired hold is reconciled in a separate transaction using its old owner's locks, then creation rechecks everything. See [reservation rules](docs/reservation-rules.md) for the transaction, conflict, and exact-deadline behavior.

No official pickup hours have been invented. `Reservations:WeeklyPickupIntervals` is empty by default, so creation returns unavailable while public browsing works. Configure confirmed hours, breaks, and closed dates using the documented reservation settings and restart the app. The default campus timezone is America/Chicago; persisted times are UTC.

Students have a **My reservations** link to `/Account/Reservations`, and successful Reserve submissions redirect there. The page separates **Current reservations** from **Recent history**, with an **Active reservations: N of 3** count. Current means stored Active with a deadline strictly later than the captured server time. All current records are shown; history independently shows at most 20 records, ordered by creation time descending and then ID descending. A stored Active row whose deadline has arrived appears as Expired in history immediately, even before cleanup runs. The count and both sections come from one owner-filtered reservation query using the same captured time. GET requests never update records or audits.

The service obtains the owner ID from authenticated claims and applies it before either query branch or the history limit. A submitted or query-string `UserId` cannot select another account. Signing in proves identity; it does not grant access to another student's records. The page and Cancel POST require Student authorization, and the service checks persisted eligibility and ownership again. Historical entries show their retained listing title, status, creation time, and deadline directly; they do not depend on public details links, which may now return 404. Older history stays in the database even when it falls outside the recent list.

Saved UTC timestamps are displayed in the configured campus timezone with its name and applicable UTC offset, including daylight-saving changes. Invalid timezone configuration falls back to labeled UTC. Cancellation uses an antiforgery-protected POST and works outside pickup hours. Unknown and other-owner IDs return the same message. At the deadline, the server records expiration instead of cancellation. Repeated processing is a no-op; each successful transition, eligible Held → Available release, and audit commit together. Missing/Collected/Archived inventory and newer holds remain protected.

The hosted expiration worker runs independently of pickup availability, using a fresh scoped service per bounded pass. `Reservations:ExpirationPollSeconds` defaults to 30 (bounded 1–300); `ExpirationBatchSize` defaults to 50 (bounded 1–200). Failed passes are logged safely and retried, and shutdown cancels pending work. The host must be running for cleanup; creation also reconciles expired holds when needed. Apply the existing reservation-index migration before launching this version. The final verification checkpoint adds no migration or production-code changes.

A reservation expires immediately at `ExpiresAtUtc <= server time`, even before cleanup runs. It stops consuming the student's limit. Read-only account queries show it as Expired; catalog queries show its known, safely releasable hold as Available, including filters/counts/details. Orphan holds or newer reservation history stay protected. These GET requests never save inventory, reservation, or audit changes. An already-open page updates on refresh; actions always recheck on the server.

## Running locally

You'll need the .NET 10 SDK and Docker. On a fresh checkout, copy .env.example to .env and choose a local database password. Set the same password in the web project's user secrets, replacing YOUR_LOCAL_PASSWORD below:

```sh
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=127.0.0.1;Port=5433;Database=luthrift;Username=luthrift;Password=YOUR_LOCAL_PASSWORD" \
  --project src/LUThrift.Web
```

Then start the database, apply the migrations (including `AddInventoryAuditEvents` and `EnforceSingleActiveReservationPerListing`), and run the app:

```sh
docker compose up -d --wait
dotnet tool restore
dotnet ef database update --project src/LUThrift.Web -- --environment Development
dotnet run --project src/LUThrift.Web --launch-profile https
```

Open https://localhost:7131. The local development certificate may need to be trusted in your browser. The automated checks below run independently of this local database.

The reservation-index migration stops and reports listing IDs if existing records contain multiple stored Active reservations for one listing. It does not delete or rewrite them. Review those conflicts before retrying. This read-only query identifies all affected listing IDs, including expired rows whose stored status is still Active:

```sql
SELECT "ListingId", COUNT(*)
FROM "Reservations"
WHERE "Status" = 'Active'
GROUP BY "ListingId"
HAVING COUNT(*) > 1;
```

### Optional development administrator

Register a local account first. To give that existing account the Administrator role during development, store its email in user secrets, replacing the placeholder:

```sh
dotnet user-secrets set "DevelopmentAdmin:Email" "YOUR_EXISTING_ACCOUNT_EMAIL" \
  --project src/LUThrift.Web
```

Restart the app with the Development environment, then log out and back in to refresh the account's role claims. The bootstrap never creates an account or sets a password. If the account does not exist, it logs a warning without the email. It does nothing outside Development, even if the setting is present.

After the role has been assigned, remove the optional setting:

```sh
dotnet user-secrets remove "DevelopmentAdmin:Email" --project src/LUThrift.Web
```

Removing the setting does not remove an already assigned role. Keep actual emails and credentials out of source files. Ordinary registration always assigns Student on the server; there is no role selector or self-service role management. The initializer checks existing roles and membership on every startup, so repeated starts do not duplicate them. If required role creation or administrator assignment fails, startup fails rather than serving the app with incomplete initialization.

### Page access

Authorization is enforced by Razor Pages folder conventions in `Program.cs`:

| Page or folder | Access |
| --- | --- |
| `/` and `/Thrift` | Public |
| `/Account` | Signed-in users; `/Account/Reserve` and `/Account/Reservations` additionally require Student |
| `/Staff` | Staff or Administrator |
| `/Admin` | Administrator |

These rules cover pages in nested folders too. Anonymous visitors to protected pages are redirected to Login. Signed-in users without the required role are redirected to Access Denied, which returns HTTP 403. Staff and Admin navigation links follow the user's roles, but the server checks access even when someone enters a URL directly.

The home page links to the working public catalog and item details. Staff has a landing page and working inventory dashboard, Create, and Edit pages. Admin has a minimal landing page. Students can view and cancel their own reservations. Profile editing, pickup, and administration placeholders remain future work. The folder rules will apply as those pages are implemented.

## Automated checks

The standard tests require the .NET 10 SDK and restored NuGet packages. They do not require Docker, PostgreSQL, or user secrets. The opt-in PostgreSQL tests are reported as skipped unless their dedicated test connection is supplied:

```sh
dotnet restore LUThrift.slnx
dotnet build LUThrift.slnx
dotnet test LUThrift.slnx
git diff --check
```

The integration tests use `Microsoft.AspNetCore.Mvc.Testing` for .NET 10. `WebApplicationFactory<ApplicationDbContext>` uses the web assembly as its entry point, so `Program` does not need to be exposed. The test host runs in the Testing environment with SQLite in memory and temporary data-protection keys. Route tests use controlled Identity cookies with the normal authentication middleware; form tests submit requests with real antiforgery tokens. These substitutions live only in the test project.

The suite covers the Staff/Admin access matrix, role-dependent navigation, Access Denied responses, registration overposting, login failures, logout protection, role initialization, development bootstrap restrictions, and registration rollback. Week 4 tests cover inventory-page access, dashboard rendering and filtering, server-side validation, unknown categories, Draft creation, edits that preserve status, forged listing/role/audit fields, every allowed and rejected lifecycle transition, and Held protection. They also check audit recording and rollback, stale listing state, missing antiforgery tokens, and requests that try to change state through GET.

Week 5 coverage includes anonymous and Student browsing, public-status filtering and detail 404s, safe text rendering, combined filters, literal search characters, stable pagination with timestamp ties, counts and page boundaries, filter/detail/back navigation, and staff lifecycle changes reflected in the catalog. Read-only checks compare inventory, audit, and reservation data before and after browsing.

The original database-model test is retained alongside audit relationship and deletion tests. Tests for future placeholder pages check folder conventions rather than claiming those routes exist. SQLite tests do not replace PostgreSQL-specific verification or a manual browser check.

### Optional local PostgreSQL verification

Use a dedicated disposable local database, never the development connection or user secrets. `CatalogPostgresTests` accepts only a loopback host and the database name `luthrift_catalog_test`. It inserts its own records inside transactions and rolls them back; it does not reset the database or delete existing rows. The checks exercise PostgreSQL case matching, literal search characters, combined filters, pagination, hidden statuses, and read-only behavior.

One way to supply that database is a temporary container with a random loopback port and no persistent volume:

```sh
docker run --rm -d --name luthrift-catalog-check \
  --publish 127.0.0.1::5432 --tmpfs /var/lib/postgresql/data:rw \
  -e POSTGRES_HOST_AUTH_METHOD=trust \
  -e POSTGRES_USER=luthrift_catalog_test -e POSTGRES_DB=luthrift_catalog_test postgres:16
docker exec luthrift-catalog-check pg_isready -U luthrift_catalog_test -d luthrift_catalog_test
docker port luthrift-catalog-check 5432/tcp
```

Wait for `pg_isready` to report accepting connections. Replace `PORT_FROM_DOCKER` with the printed port, then run the complete suite:

```sh
LUTHRIFT_CATALOG_TEST_CONNECTION='Host=127.0.0.1;Port=PORT_FROM_DOCKER;Database=luthrift_catalog_test;Username=luthrift_catalog_test' \
  dotnet test LUThrift.slnx
docker stop luthrift-catalog-check
```

The password-free setup above is only for this disposable database bound to loopback. It is not the application database setup. Stopping the container removes its temporary storage.

### Optional PostgreSQL reservation verification

`ReservationConcurrencyTests` requires `LUTHRIFT_RESERVATION_TEST_CONNECTION`, a loopback host, and the dedicated database name `luthrift_reservation_test`. Each case creates a randomly named schema, applies the real migrations, and drops only its own schema. It never uses user secrets, resets the development database, or deletes existing records. The database user needs permission to create and drop these test schemas.

For a disposable PostgreSQL 18 instance with no persistent storage:

```sh
docker run --rm -d --name luthrift-reservation-check \
  --publish 127.0.0.1::5432 --tmpfs /var/lib/postgresql:rw \
  -e POSTGRES_HOST_AUTH_METHOD=trust \
  -e POSTGRES_USER=luthrift_reservation_test -e POSTGRES_DB=luthrift_reservation_test postgres:18-bookworm
docker exec luthrift-reservation-check pg_isready -U luthrift_reservation_test -d luthrift_reservation_test
docker port luthrift-reservation-check 5432/tcp
```

After `pg_isready` reports accepting connections, create the separate catalog test database and substitute the printed port in both connections. Build first using the commands above. This runs both optional suites without skips:

```sh
docker exec luthrift-reservation-check createdb -U luthrift_reservation_test luthrift_catalog_test
LUTHRIFT_RESERVATION_TEST_CONNECTION='Host=127.0.0.1;Port=PORT_FROM_DOCKER;Database=luthrift_reservation_test;Username=luthrift_reservation_test' \
LUTHRIFT_CATALOG_TEST_CONNECTION='Host=127.0.0.1;Port=PORT_FROM_DOCKER;Database=luthrift_catalog_test;Username=luthrift_reservation_test' \
  dotnet test LUThrift.slnx --no-build --logger 'console;verbosity=minimal'
docker stop luthrift-reservation-check
```

Trust authentication here is confined to this temporary loopback database. PostgreSQL race tests commit their fixtures first, then run operations on separate connections and DbContexts. A shared start signal launches the competing requests; a separate blocker holds a user or listing row until every request is observed waiting on a database lock. Releasing that blocker lets the real service transactions compete. There is no shared outer transaction around those operations.

These tests cover two students requesting one item, one student near the limit requesting different items, simultaneous duplicate submissions, and cancellation/expiration competing with a replacement reservation. They also verify newer-hold protection, audit-failure rollback, the filtered index, and migration refusal without data loss. SQLite and HTTP tests cover exact deadlines, owner-only POSTs, antiforgery, safe input, read-only displays, bounded worker passes, retry, shutdown, and rollback. The optional catalog PostgreSQL suite also verifies the effective expired-hold projection against the real provider.

Checkpoint 2 verification on **October 25, 2026**: restore passed; the solution build had **0 warnings and 0 errors**; the complete suite with the reservation PostgreSQL connection passed **376 tests, 0 failed, 2 skipped, 378 total**. All eight reservation PostgreSQL cases ran on a dedicated disposable PostgreSQL **18.6** instance. The two optional catalog PostgreSQL tests were skipped because their separate connection was not supplied. All test schemas were removed afterward. The safe EF check reported no pending model changes and `git diff --check` passed. A read-only development-database check found zero conflicting listing groups; the new migration was not applied to that database during verification.

Checkpoint 3 verification on **October 26, 2026**: restore passed, build finished with **0 warnings and 0 errors**, and the complete suite passed **456 tests, 0 failed, 0 skipped** with both dedicated PostgreSQL connections enabled. The 18 PostgreSQL checks included actual lock contention, cancellation crossing its deadline while waiting, concurrent expiration/cancellation, expired-hold reconciliation, rollback, and read-only catalog projection. SQLite and HTTP checks covered exact deadlines, owner-only cancellation, antiforgery, repeated processing, newer holds, bounded worker passes, failure retry, and shutdown. The safe EF check reported no model changes, and `git diff --check` passed. No development-database data was changed; the temporary test schemas and container were cleaned up. The new account page was verified through HTTP tests; a manual browser walkthrough was not performed.

Checkpoint 4 verification on **October 27, 2026**: restore passed, build finished with **0 warnings and 0 errors**, and the complete suite passed **491 tests, 0 failed, 0 skipped**, including the existing 18 checks against disposable PostgreSQL databases. The new 26 HTTP cases cover protected creation, antiforgery, forged fields, stale availability, pickup-hour changes, limits, duplicate submissions, conflict feedback, and GET requests without writes. Nine formatter cases cover saved UTC values, winter/summer offsets, repeated daylight-saving times, and invalid-timezone fallback. The safe EF check reported no model changes, and `git diff --check` passed. No new migration or development-database changes were needed. Manual browser checks for this checkpoint remain pending; use the checklist below.

Checkpoint 5 verification on **October 27, 2026**: restore passed, build had **0 warnings and 0 errors**, and the complete suite passed **510 tests, 0 failed, 0 skipped**, with both dedicated PostgreSQL connections enabled. New coverage includes six HTTP tests using two separately signed-in Students, 12 SQLite overview-query cases, and one PostgreSQL owner/history-limit query case. The tests verify count updates, strict deadline boundaries, bounded and stably ordered history, private historical item titles, forged owner/ID requests, missing and cross-user antiforgery tokens, repeated cancellation, and unchanged data during GETs. All 19 PostgreSQL checks passed against disposable databases; development data was untouched. The safe EF check found no model changes, and `git diff --check` passed. No migration was added. This verification used HTTP tests rather than a manual browser walkthrough.

### Week 6 final verification

Checkpoint 6 on **October 27, 2026**: `dotnet restore LUThrift.slnx` passed; `dotnet build LUThrift.slnx` passed with **0 warnings and 0 errors**. The full `dotnet test LUThrift.slnx --no-build --logger 'console;verbosity=minimal'` run, with both dedicated test connections enabled, passed **514 tests, 0 failed, 0 skipped**. This includes **19 reservation and 3 catalog PostgreSQL cases** on a disposable PostgreSQL **18.6** instance. The safe EF check reported no changes since the last migration; `git diff --check` passed. All reservation test schemas were removed, and the disposable container was stopped afterward. No development-database records or configuration were changed.

Four regression tests were added: simultaneous duplicate submissions; cancellation and expiration racing a replacement at the exact deadline; cancellation before the deadline racing a new request; and an HTTP reserve → view → cancel → expire → reserve-again flow. The HTTP test fills three slots through real form submissions, rejects the fourth, reuses a canceled slot, and proves overdue stored Active rows stop consuming the limit before cleanup. It checks retained history, exact saved deadlines, expected audits, and unchanged data during catalog/account GETs. The PostgreSQL migration tests apply `EnforceSingleActiveReservationPerListing`, exercise its `"Status" = 'Active'` unique index, and confirm conflicting history causes migration refusal without deleting data. No production-code fix was needed.

- [x] One-hour duration, opening/closing boundaries, breaks, closures, timezone changes, and exact expiration covered by controlled-time tests.
- [x] Three-active limit, canceled/expired slot reuse, active-account eligibility, ownership, antiforgery, and forged fields covered.
- [x] Atomic writes and rollback, safe hold release, repeated processing, worker retry/shutdown, and all four concurrency scenarios verified.
- [x] Public browsing remains read-only; existing catalog and Staff/Administrator tests still pass.
- [x] Restore, build, full tests, PostgreSQL migration/index verification, EF model check, and whitespace check passed.
- [ ] Two-account Week 6 browser walkthrough, mobile layout, keyboard/focus, and real-time expiration remain pending. HTTP tests are not a visual browser check.

The implemented student workflow is automatically verified, but Week 6 is not fully browser-verified. Official pickup hours are still **unconfirmed**, so the shipped empty schedule disables new reservations. Staff pickup confirmation, operational monitoring/repair procedures, and real image upload remain future work.

### Checking the EF model

To compare the EF model with the migration snapshot without connecting to the development database, restore the local tool and use a placeholder connection in the Testing environment:

```sh
dotnet tool restore
ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=1;Database=luthrift_model_check;Username=model_check;Timeout=1' \
  dotnet ef migrations has-pending-model-changes --project src/LUThrift.Web --no-build -- --environment Testing
```

The custom account workflow includes registration, login, logout, access denial, and viewing/canceling owned reservations. Email delivery, campus sign-in, account deactivation, and profile editing remain future work. The Identity UI package still supplies its other standard pages; their presence does not mean email delivery or those application features have been completed.

### Week 6 browser walkthrough — pending

No Week 6 manual browser run was performed in checkpoint 6. The earlier Week 5 browser record does not verify the new reservation UI. Use the documented Development HTTPS setup against a separate local test database with migrations applied. Configure explicitly synthetic pickup intervals through local configuration for this walkthrough, with enough time for the complete hour; do not present them as official hours or commit them. Keep the one-hour policy and system clock unchanged.

1. Register two synthetic accounts, A and B, and use separate browser profiles/private windows so their cookies stay separate. Both should receive Student. For fixture setup, the optional development bootstrap can temporarily add Administrator to A's existing account; restart and sign in again. A retains Student. Create and publish at least five clearly labeled test items through Staff inventory, including one with a long title and missing size. Leave another item Draft and confirm it stays hidden.
2. Signed out, open `/Thrift` and Available details. **Sign in to reserve** should return to that item without creating a reservation. As A, reserve one item and check the redirect to My reservations, confirmation, title, count **1 of 3**, and saved deadline with campus timezone and UTC offset. Refresh: no duplicate or later deadline should appear.
3. As B, the same item should show Reserved without A's identity. My reservations should show only B's records. Reserve a different item as B and verify A's page does not show it. With B's own valid Cancel form token, changing the submitted ID to A's reservation must give the same safe response as an unknown ID, leaving both holds unchanged. A missing token must reject the POST.
4. As A, reserve until the count is **3 of 3**; a fourth must be refused. Cancel one through its form. Check **2 of 3**, retained Canceled history, and the released item's Available status. Retry the old Cancel submission: no second change. Reserve another item successfully. Reposting an old Reserve form must not duplicate the hold or extend its deadline.
5. Leave a reservation until its actual displayed deadline. Refresh My reservations just before and at/after that time: it must leave Current, show Expired in history, lose its Cancel control, and stop consuming the limit. Check that the item becomes Available and B can reserve it. A's stale Cancel request must not release B's new hold. Worker cleanup normally follows within a polling pass; the exact boundary is already covered by controlled-time tests and does not depend on catching a browser refresh at an exact tick.
6. Keep an active reservation, then restart with an empty or closed synthetic schedule. New creation must be unavailable while browsing, cancellation, and eventual expiration still work. Also check a window with less than one hour remaining. Restore only the intended local settings afterward. For stale availability, open a form as A and let B reserve the item before A submits; A must receive safe feedback.
7. Repeat details, My reservations, cancellation messages, and navigation at about **375px, 768px, and desktop width**. Check long titles and timezone labels for overflow, readable status/deadline text, keyboard access to all controls, visible focus, and an understandable Cancel label. Confirm Student B cannot open Staff/Admin pages, and successful Staff lifecycle actions still affect the public catalog for unheld items.

Record the browser/version, widths, results, and any defects before marking this walkthrough complete. Retain canceled/expired history; archive the test inventory after holds finish rather than deleting listings. Staff pickup confirmation is not part of this walkthrough because it is not implemented.

### Manual Week 4 check

Use the local Development setup above and disposable inventory records:

1. While signed out, visit `/Staff/Inventory` and confirm it sends you to Login. Register a development account through `/Identity/Account/Register`. That ordinary Student account should receive Access Denied when opening inventory, Create, or Edit directly.
2. Follow **Optional development administrator** above: store the existing account's email in `DevelopmentAdmin:Email`, restart in Development, then log out and back in. Confirm Staff navigation opens inventory. Remove the optional setting after assignment; the role stays assigned.
3. Open Create. Submit missing required fields and an overlong title with client validation bypassed; confirm errors and category options remain. Submit valid fields and confirm a Draft listing and a success message. Edit it and confirm the status stays Draft. Check the image placeholder and status filter.
4. Publish that listing, edit it while Available, mark it missing, edit it while Missing, then archive it. Confirm each status on the dashboard. Use separate listings to check Available → Archived, Draft → Archived, and Available → Collected through **Record walk-in collection**.
5. Confirm Archived and Collected records remain listed but have no Edit or lifecycle buttons. Refresh after a successful POST and confirm it does not repeat the action. The automated tests check forged requests and Held protection; there is deliberately no UI action that creates a Held listing.
6. Inspect `AuditEvents` with a local database viewer to confirm the listing, actor ID, action, UTC time, and transition details. There is no audit-history page yet. Keep the account email and all local credentials out of Git.

### Week 5 browser checklist

Use disposable inventory records and the existing local Development setup. The checklist below was completed on October 25, 2026 in Chrome with viewport emulation, keyboard input, and screenshot inspection. These are browser checks against the running Development app, separate from the HTTP test suite.

- [x] At approximately **375px**, **768px**, and a desktop width, inspect the home page, catalog, filtered results, and details. Check card layout, long titles/descriptions, missing sizes, placeholder images, and horizontal overflow.
- [x] Use only the keyboard to open the mobile navigation, reach Browse items, operate every filter, submit, clear, follow Previous/Next, open details, and return. Check visible focus, labels, selected values, and disabled pagination controls.
- [x] Repeat browsing signed out and as an ordinary Student. Confirm Student access to Staff/Admin URLs is denied and signed-out access redirects to Login.
- [x] Combine title, category, size, and availability filters with at least 25 matching temporary items. Browse three pages, open details from page 2, and return to the same selections and page. Submit different filters and confirm page 1. Check empty results and malformed/unknown detail URLs.
- [x] As Staff or Administrator, create a temporary **Draft** and confirm it is absent from the catalog and returns 404 by direct detail URL. Publish it and confirm it appears. Using separate published items, mark one **Missing**, record one walk-in **Collected**, and **Archive** another; confirm each disappears and its detail URL returns 404. Retain these records and their history instead of deleting them; Missing items can subsequently be archived, while Collected is terminal.
- [x] Confirm Held appears as Reserved only in the appropriate filter and direct public details, with no owner information. Use a prepared test fixture for Held; there is intentionally no staff action to place an item on hold.

### Week 5 verification record

On **October 20, 2026**, `dotnet restore LUThrift.slnx` passed, `dotnet build LUThrift.slnx` finished with **0 warnings and 0 errors**, and the complete `dotnet test LUThrift.slnx --no-build` run passed **277 tests, 0 failed, 0 skipped**, with `LUTHRIFT_CATALOG_TEST_CONNECTION` pointing to a disposable local PostgreSQL 16 database. This includes both PostgreSQL tests as well as the isolated SQLite and HTTP tests. The documented EF model check reported **no changes since the last migration**.

The October 20 whitespace check passed, but the temporary Testing host did not serve its CSS and JavaScript correctly. That browser attempt was not counted as a pass.

On **October 25, 2026**, verification used the documented **Development** HTTPS launch profile against local PostgreSQL **18.6**. The existing `AddInventoryAuditEvents` migration first had to be applied using the setup command above. No new migration was created. An existing HTTP process occupied port 5278, so the verification host used `dotnet run --project src/LUThrift.Web --launch-profile https -- --urls https://localhost:7131`, leaving that process alone. Certificate validation was bypassed only in the isolated localhost verification browser and requests; system trust was not changed.

All seven rendered asset URLs returned **HTTP 200** with the expected content types: Bootstrap CSS and JavaScript, site CSS and JavaScript, scoped CSS, jQuery, and the placeholder SVG. The empty `site.js` is intentional. Chrome loaded the styles and images, and the Bootstrap navigation toggle worked. The earlier asset failure came from the standalone Testing launch setup; no application static-asset change was needed.

The browser checklist passed in **Chrome 155.0.8059.85**, driven through its debugging protocol, at **375px, 768px, and 1440px**. Screenshots and measured page bounds showed no horizontal overflow, including a 150-character title, 2,000-character description, and missing size. Keyboard checks covered the mobile menu, every filter, submit/clear, paging, details, and return links; focus indicators remained visible after their CSS transitions. Native dropdowns were operated with keyboard type-ahead. These checks used emulated viewport sizes, not physical mobile devices, and do not claim Safari, Firefox, or screen-reader coverage.

Signed-out and Student browsing passed, including Staff/Admin login redirects or access denial. Combined filters on PostgreSQL matched case differences and literal `%`, `_`, and `!`, returned 25 items across pages of **12, 12, and 1**, and preserved selections through details and back navigation. Empty results, invalid filters, page boundaries, hidden/unknown detail 404s, encoded text, and Reserved visibility passed. Database snapshots before and after browsing matched for all listings, audit events, and reservations.

Three further listings were created and published through the Staff browser forms, then respectively marked Missing, Collected, and Archived. Each appeared only while Available, and PostgreSQL recorded the nine expected creation/transition audits with the staff actor and UTC timestamps. The four original listings remained unchanged. After approved cleanup, all 36 labelled fixtures remain as Archived or Collected records with their audit history, and the two synthetic accounts have been removed. The single prepared Held fixture was archived by a scoped maintenance query with its own audit event; this did not add or exercise a staff Held transition. No listing was deleted and no existing inventory was reset.

The October 25 restore passed; the build finished with **0 warnings and 0 errors**. `dotnet test LUThrift.slnx --no-build --logger 'console;verbosity=minimal'` passed **275 tests, 0 failed, 2 skipped, 277 total**. The two optional PostgreSQL tests were skipped because their dedicated test connection was not supplied for this run; the browser checks above used the actual Development PostgreSQL database. The documented safe EF check again reported **no changes since the last migration**, and `git diff --check` passed. No application-code change was needed during this browser review.

Week 5 acceptance:

- [x] Anonymous and Student catalog/details access; Staff and Administrator protections retained.
- [x] Public visibility, encoded listing text, missing sizes, empty results, and invalid-route behavior covered by passing tests.
- [x] Combined search/filters, pagination, and details/back navigation covered by passing SQLite/HTTP and PostgreSQL checks.
- [x] Inventory, audit, and reservation snapshots unchanged by browsing; staff lifecycle visibility covered by passing HTTP tests.
- [x] Restore, build, complete test suite, whitespace check, and safe pending-model-change check passed.
- [x] Responsive layouts, keyboard navigation, visible focus, overflow, and the browser lifecycle walkthrough completed with working assets.

Week 5's automated and browser acceptance checks are complete within the scope recorded above. Details are recorded in [the development log](docs/development-log.md). Reservations remain Week 6 work; real image upload remains a later checkpoint.

## Documentation

- [Requirements](docs/requirements.md)
- [Reservation rules](docs/reservation-rules.md)
- [Development log](docs/development-log.md)

Repository name: THRIFTLU.
