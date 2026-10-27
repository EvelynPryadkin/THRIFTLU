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

### Reservations (Week 6 checkpoints 1–4)

Signed-out visitors can browse and open details, which offer a sign-in link returning to that public item GET. An active Student sees **Reserve item** for an Available item when pickup eligibility permits a full hour. Closed, unconfigured, and insufficient pickup time each have an explanation. The display is a hint; it does not promise availability or bypass the reservation limit.

The form posts only `ListingId` and its antiforgery token to `/Account/Reserve`. The entire receiving page requires Student authorization under the protected Account folder; no creation handler lives under the anonymous Thrift folder. `ReservationService.CreateAsync` receives the authenticated server principal and rechecks the persisted account's active state and Student membership, configured pickup hours, Available status, and the three-active-reservation limit. It saves a one-hour Active reservation, the Held listing status, and a `ListingReserved` audit together. Submitted owner IDs, roles, statuses, durations, or timestamps are ignored because they are absent from the input model.

Business outcomes redirect to My reservations with a useful message, including stale availability, duplicate submissions, limits, closed hours, and conflicts. Successful confirmation displays the deadline returned by the committed service operation. It never adds an hour to the browser's clock or to a later GET. A GET to `/Account/Reserve` only redirects to browsing and cannot replay a reservation.

PostgreSQL transactions lock the student account first, then the listing, so both same-student and same-item races are coordinated across application instances. Time is read after lock waits and checked again before saving. A filtered unique index also prevents multiple stored Active reservations for one listing. Before creation, an expired hold is reconciled in a separate transaction using its old owner's locks, then creation rechecks everything. See [reservation rules](docs/reservation-rules.md) for the transaction, conflict, and exact-deadline behavior.

No official pickup hours have been invented. `Reservations:WeeklyPickupIntervals` is empty by default, so creation returns unavailable while public browsing works. Configure confirmed hours, breaks, and closed dates using the documented reservation settings and restart the app. The default campus timezone is America/Chicago; persisted times are UTC.

Students have a **My reservations** link to `/Account/Reservations`. The page shows their active reservations first, followed by recent history, up to 100 records. Saved UTC timestamps are displayed in the configured campus timezone with its name and the applicable UTC offset, including daylight-saving changes. If timezone configuration later becomes invalid, existing records fall back to clearly labeled UTC. Cancellation is an antiforgery-protected POST with ownership checked in the service. It works outside pickup hours; unknown and other-owner IDs return the same message. At the deadline, the server records expiration instead of cancellation. Repeated processing is a no-op, and each successful transition, eligible Held → Available release, and audit commit together. Missing/Collected/Archived inventory and newer holds remain protected.

The hosted expiration worker runs independently of pickup availability, using a fresh scoped service per bounded pass. `Reservations:ExpirationPollSeconds` defaults to 30 (bounded 1–300); `ExpirationBatchSize` defaults to 50 (bounded 1–200). Failed passes are logged safely and retried, and shutdown cancels pending work. The host must be running for cleanup; creation also reconciles expired holds when needed. Apply the existing reservation-index migration before launching this version. No new migration is required for checkpoint 3.

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

After `pg_isready` reports accepting connections, substitute the printed port:

```sh
LUTHRIFT_RESERVATION_TEST_CONNECTION='Host=127.0.0.1;Port=PORT_FROM_DOCKER;Database=luthrift_reservation_test;Username=luthrift_reservation_test' \
  dotnet test LUThrift.slnx
docker stop luthrift-reservation-check
```

Trust authentication here is confined to this temporary loopback database. PostgreSQL tests observe actual lock waits for creation, cancellation versus expiration, and expired-hold reconciliation. They also verify newer-hold protection, audit-failure rollback, the filtered index, and migration refusal without data loss. SQLite and HTTP tests cover exact deadlines, owner-only POSTs, antiforgery, safe input, read-only displays, bounded worker passes, retry, shutdown, and rollback. The optional catalog PostgreSQL suite also verifies the effective expired-hold projection against the real provider.

Checkpoint 2 verification on **October 25, 2026**: restore passed; the solution build had **0 warnings and 0 errors**; the complete suite with the reservation PostgreSQL connection passed **376 tests, 0 failed, 2 skipped, 378 total**. All eight reservation PostgreSQL cases ran on a dedicated disposable PostgreSQL **18.6** instance. The two optional catalog PostgreSQL tests were skipped because their separate connection was not supplied. All test schemas were removed afterward. The safe EF check reported no pending model changes and `git diff --check` passed. A read-only development-database check found zero conflicting listing groups; the new migration was not applied to that database during verification.

Checkpoint 3 verification on **October 26, 2026**: restore passed, build finished with **0 warnings and 0 errors**, and the complete suite passed **456 tests, 0 failed, 0 skipped** with both dedicated PostgreSQL connections enabled. The 18 PostgreSQL checks included actual lock contention, cancellation crossing its deadline while waiting, concurrent expiration/cancellation, expired-hold reconciliation, rollback, and read-only catalog projection. SQLite and HTTP checks covered exact deadlines, owner-only cancellation, antiforgery, repeated processing, newer holds, bounded worker passes, failure retry, and shutdown. The safe EF check reported no model changes, and `git diff --check` passed. No development-database data was changed; the temporary test schemas and container were cleaned up. The new account page was verified through HTTP tests; a manual browser walkthrough was not performed.

Checkpoint 4 verification on **October 27, 2026**: restore passed, build finished with **0 warnings and 0 errors**, and the complete suite passed **491 tests, 0 failed, 0 skipped**, including the existing 18 checks against disposable PostgreSQL databases. The new 26 HTTP cases cover protected creation, antiforgery, forged fields, stale availability, pickup-hour changes, limits, duplicate submissions, conflict feedback, and GET requests without writes. Nine formatter cases cover saved UTC values, winter/summer offsets, repeated daylight-saving times, and invalid-timezone fallback. The safe EF check reported no model changes, and `git diff --check` passed. No new migration or development-database changes were needed. Manual browser checks for this checkpoint remain pending; use the checklist below.

### Checking the EF model

To compare the EF model with the migration snapshot without connecting to the development database, restore the local tool and use a placeholder connection in the Testing environment:

```sh
dotnet tool restore
ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=1;Database=luthrift_model_check;Username=model_check;Timeout=1' \
  dotnet ef migrations has-pending-model-changes --project src/LUThrift.Web --no-build -- --environment Testing
```

The custom account workflow includes registration, login, logout, access denial, and viewing/canceling owned reservations. Email delivery, campus sign-in, account deactivation, and profile editing remain future work. The Identity UI package still supplies its other standard pages; their presence does not mean email delivery or those application features have been completed.

### Manual student Reserve checklist

These steps are a checklist, not a claim that a browser walkthrough has been performed for checkpoint 4. Use the documented Development setup and disposable listings. Configure confirmed pickup hours, or explicitly synthetic local test hours, through local configuration; the shipped empty schedule intentionally disables creation.

1. Signed out, open `/Thrift` and an Available item's details. Confirm browsing stays public and **Sign in to reserve** returns to that item after login without reserving it.
2. As an active Student during eligible pickup hours, confirm the form offers **Reserve item**. Submit it and check the redirect to My reservations, confirmation, saved deadline, campus timezone, and UTC offset. Refresh and confirm no second reservation appears.
3. Open the item again and confirm it is Reserved. Resubmit the original form from another tab and confirm a useful duplicate/unavailable message without extending the deadline.
4. With three active reservations, try another Available item and confirm the limit message. Cancel one through My reservations and confirm another reservation becomes possible.
5. Load details in one tab, then have another Student reserve that item, or have Staff archive it, before submitting the original form. Confirm the stale form cannot reserve it.
6. Check closed pickup hours, less than one hour remaining, and an empty schedule. Confirm explanations and no Reserve button. A form loaded earlier must still be rejected by the POST when its eligibility changes.
7. Confirm a Staff-only account cannot POST to `/Account/Reserve`, and signed-out access redirects to Login. Check keyboard access to the sign-in/Reserve controls and long-text layout at mobile width. The automated HTTP suite separately verifies missing antiforgery tokens and forged fields.

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
