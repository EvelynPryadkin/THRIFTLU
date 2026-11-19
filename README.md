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

Accounts, staff inventory, the public catalog, student reservations, and staff pickup handling are implemented through Week 7. The home page is a landing page with a Browse items link to `/Thrift`. Week 8 adds listing photos, request protection, safe error responses, and health checks. The November 12 full suite passed all 883 tests, including both optional PostgreSQL suites. The [November 9/11 browser record](docs/week8-browser-verification.md) remains separate from the [final automated verification](#week-8-final-verification); its oversized-upload feedback defect has been fixed and checked through HTTP tests and a disposable Development host.

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

Week 8 supports one optional local JPEG/PNG photo per listing, managed from Staff Edit and displayed in public catalog cards and details. See [photo storage setup](docs/image-storage.md). Missing photos use the SVG placeholder; the staff inventory table still uses its placeholder even when an uploaded photo exists. Rejected uploads preserve the previous photo, and requests above the multipart/request cap now receive photo-size and refresh guidance. Week 6 includes student reservation creation, owner cancellation, and background expiration. Week 7 adds staff lookup, pickup confirmation, and reporting a reserved item missing. Held belongs to the reservation workflow and cannot be edited or changed by the Week 4 inventory actions. Collected and Archived are terminal for the staff workflow. There is no audit-history screen or staff-role management screen yet. Pickup hours, room procedures, campus sign-in, hosting, and maintenance are **To Be Confirmed**.

### Public catalog

Signed-out visitors and signed-in users can browse `/Thrift` and open `/Thrift/Details/{id}`. Cards show the title, category, condition, optional size, availability, and uploaded photo or placeholder. Details also show the description. Missing sizes say **Not specified**, and listing text uses Razor's normal HTML encoding. Details show Held as **Reserved**, without identifying who reserved it. Unknown IDs and Draft, Missing, Collected, or Archived details return HTTP 404.

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

No official pickup hours have been invented. New reservations use the persisted schedule; missing or empty hours keep creation unavailable while public browsing works. Active Administrators can use **Admin → Pickup hours** (`/Admin/PickupHours`) to save confirmed weekly intervals, breaks, closed dates, and the campus timezone. The default timezone is America/Chicago; reservation timestamps remain UTC.

Schedule edits use version checks and commit with an administrative audit. New requests see committed edits without restarting. Each reservation operation captures one schedule before reconciliation and locking, then checks fresh server time against that same snapshot after lock waits and before saving. An operation that captured the previous schedule may finish using it; the next operation reads the new schedule. Changing hours never rewrites existing deadlines or disables cancellation, expiration, or valid staff pickup. Startup and GET requests never import settings. Existing configured hours require an explicit reviewed import through the Administrator page; see [schedule storage and import](docs/pickup-schedule-storage.md). The one-hour duration and three-active-reservation limit remain fixed in `ReservationPolicy`.

Students have a **My reservations** link to `/Account/Reservations`, and successful Reserve submissions redirect there. The page separates **Current reservations** from **Recent history**, with an **Active reservations: N of 3** count. Current means stored Active with a deadline strictly later than the captured server time. All current records are shown; history independently shows at most 20 records, ordered by creation time descending and then ID descending. A stored Active row whose deadline has arrived appears as Expired in history immediately, even before cleanup runs. The count and both sections come from one owner-filtered reservation query using the same captured time. GET requests never update records or audits.

The service obtains the owner ID from authenticated claims and applies it before either query branch or the history limit. A submitted or query-string `UserId` cannot select another account. Signing in proves identity; it does not grant access to another student's records. The page and Cancel POST require Student authorization, and the service checks persisted eligibility and ownership again. Historical entries show their retained listing title, status, creation time, and deadline directly; they do not depend on public details links, which may now return 404. Older history stays in the database even when it falls outside the recent list.

Saved UTC timestamps are displayed in the configured campus timezone with its name and applicable UTC offset, including daylight-saving changes. Invalid timezone configuration falls back to labeled UTC. Cancellation uses an antiforgery-protected POST and works outside pickup hours. Unknown and other-owner IDs return the same message. At the deadline, the server records expiration instead of cancellation. Repeated processing is a no-op; each successful transition, eligible Held → Available release, and audit commit together. Missing/Collected/Archived inventory and newer holds remain protected.

The hosted expiration worker runs independently of pickup availability, using a fresh scoped service per bounded pass. `Reservations:ExpirationPollSeconds` defaults to 30 (bounded 1–300); `ExpirationBatchSize` defaults to 50 (bounded 1–200). Failed passes are logged safely and retried, and shutdown cancels pending work. The host must be running for cleanup; creation also reconciles expired holds when needed. Apply the existing reservation-index migration before launching this version. The final verification checkpoint adds no migration or production-code changes.

A reservation expires immediately at `ExpiresAtUtc <= server time`, even before cleanup runs. It stops consuming the student's limit. Read-only account queries show it as Expired; catalog queries show its known, safely releasable hold as Available, including filters/counts/details. Orphan holds or newer reservation history stay protected. These GET requests never save inventory, reservation, or audit changes. An already-open page updates on refresh; actions always recheck on the server.

### Staff pickups (Week 7)

The Staff landing page links to `/Staff/Pickups`. The existing Staff-or-Administrator folder policy protects the page; `StaffPickupService` also verifies the account is currently active and still has a Staff or Administrator role in the database before returning student information. A stale role cookie is insufficient. Public catalog pages never show reservation owners, names, or emails.

The dashboard defaults to Active reservations whose deadlines are still in the future. Staff can search by exact reservation ID or student email, including partial email text, with both filters applied together. Results show the item, student display name/email, reservation ID, statuses, and campus-time deadline. They are paginated in the database in groups of 20, ordered by deadline and then ID. Exact ID lookup can explain completed or expired records, but cannot make them collectible. Invalid searches show a validation message; all these reads leave reservations, listings, and audits unchanged.

An eligible row offers two antiforgery-protected POST forms, each submitting only the reservation ID. The PageModel passes the authenticated principal to `ReservationService`, which checks current staff eligibility again. It locks the **student owner**, then the listing, rereads the hold, and uses fresh server time after lock waits and immediately before the transition. Pickup uses the existing deadline, so an unexpired hold can be collected near closing without meeting creation's full-hour requirement.

| Action | Reservation | Listing | Saved information |
| --- | --- | --- | --- |
| Confirm pickup | Active → Fulfilled | Held → Collected | Server UTC `FulfilledAtUtc` and staff-attributed audit |
| Report item missing | Active → Canceled | Held → Missing | Controlled student-friendly reason and staff-attributed audit |

Each row transition and its audit commit in one transaction. Missing-item handling never releases the listing to Available between steps. Failed writes roll everything back. Repeated, expired, inconsistent, or superseded requests cannot change a newer hold or add another audit. The forms ignore submitted identities, statuses, reasons, and timestamps. Success redirects to the saved record; refreshing that GET cannot repeat the action.

My reservations removes these completed records from the active count and keeps their titles in owned history. A pickup shows its saved fulfillment time; a missing item shows only the controlled explanation, without internal audit details. Both Collected and Missing items disappear from public results and return 404 from public details. Staff pickup procedures, official hours, and responsibility for monitoring cleanup failures still need agreement; the strict expiration deadline has no late override.

### Photos and operational safeguards (Week 8)

Photo uploads accept decoded **JPEG or PNG**, up to **5 MiB (5,242,880 bytes)**, **20,000,000 decoded pixels**, and **8192 pixels per side**. SkiaSharp 4.154.0 (MIT) normalizes orientation and re-encodes the pixels without uploaded metadata. The Edit page has a separate 6 MiB request/multipart cap. Staff photo operations recheck current database permissions and listing editability, use server-generated filenames, and reject stale replacements. They retain the previous photo on a failed replacement. Image URLs recheck visibility on every request and use `no-store`; collected/archived photos return 404, including for staff.

Configure an application-owned directory outside the repository and `wwwroot`. On macOS:

```sh
mkdir -p "$HOME/Library/Application Support/LUThrift/listing-images"
chmod 700 "$HOME/Library/Application Support/LUThrift/listing-images"
dotnet user-secrets set "ImageStorage:RootPath" \
  "$HOME/Library/Application Support/LUThrift/listing-images" \
  --project src/LUThrift.Web
```

Missing storage configuration disables photo writes while browsing still works. Use persistent storage across deployments/restarts, including a mounted persistent volume for containers; do not keep production photos in a disposable container layer. Back up the directory and database together. Multiple app instances must share the same files. File writes and database commits are coordinated but are not one distributed transaction: a crash or uncertain commit can leave an unreferenced file, and cleanup failures are logged. Operator reconciliation must preserve referenced files and investigate uncertain operations with writers stopped. See [storage behavior and limits](docs/image-storage.md); automatic orphan cleanup and its operational owner remain unconfirmed.

Built-in POST rate limits have independent fixed windows and no queue:

| Group | Covered actions | Default |
| --- | --- | --- |
| Login | Sign in | 10 per 60 seconds |
| Registration | Register | 5 per 600 seconds |
| Reservations | Reserve and Cancel, shared | 20 per 60 seconds |
| Pickups | Confirm pickup and Report missing, shared | 30 per 60 seconds |
| ImageUploads | Upload/replace photo | 10 per 60 seconds |

Configure `RequestProtection:{Group}:PermitLimit` and `WindowSeconds`, then restart. The key is the authenticated user ID, otherwise the connection IP; arbitrary forwarded-IP headers are not trusted. Rejections return **429** with `Retry-After` and safe retry guidance. Browsing, image GETs, static assets, and health probes are unaffected. Budgets are in memory per instance and reset on restart. See [request protection](docs/request-protection.md) for bounds and reverse-proxy considerations. Production Identity cookies are Secure, HttpOnly, and SameSite=Lax; Development preserves the local HTTP and HTTPS profiles. Content-type, referrer, and framing headers also apply to errors.

Error pages preserve HTTP status codes, offer navigation and a request reference, and do not reveal exception details. Application request logs keep correlation IDs, status, and duration without raw URLs/search terms, credentials, cookies, tokens, student emails, uploaded content, or connection strings. Audit history and expiration-failure reporting remain intact.

| Health endpoint | Meaning | Response |
| --- | --- | --- |
| `/health/live` | Process answers HTTP | `200 Healthy` |
| `/health/ready` | Database connectivity, with a five-second timeout | `200 Healthy` or `503 Unhealthy` |

Both probes are anonymous, minimal, and `no-store`. Readiness does **not** prove migrations are applied, photo storage is healthy, or reservation cleanup is succeeding. The startup role initializer still needs a database connection before the app can serve these probes. See [safe errors, logging, and local checks](docs/operational-safeguards.md). Official pickup hours, physical identity-check procedures, and cleanup-monitoring ownership remain unconfirmed.

## Running locally

You'll need the .NET 10 SDK and Docker. On a fresh checkout, copy .env.example to .env and choose a local database password. Set the same password in the web project's user secrets, replacing YOUR_LOCAL_PASSWORD below:

```sh
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=127.0.0.1;Port=5433;Database=luthrift;Username=luthrift;Password=YOUR_LOCAL_PASSWORD" \
  --project src/LUThrift.Web
```

Then start the database, apply the migrations (including `AddInventoryAuditEvents`, `EnforceSingleActiveReservationPerListing`, `AddListingImages`, `AddAdministrativeAuditEvents`, and `PersistPickupSchedules`), and run the app:

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

The home page links to the working public catalog and item details. Staff has working inventory, Create, Edit, and Pickups pages. Students can view and cancel their own reservations and see completed pickup or missing-item history. Administrators can manage Staff access and pickup hours. Profile editing and other store settings remain future work.

Active Administrators can open **Admin → Manage Staff access** (`/Admin/Users`) to search existing accounts by display name or email. Searches are limited to 100 characters, and results are paginated in groups of 20. The page grants or removes only Staff membership and preserves Student membership. Administrator accounts cannot be changed here; this page does not grant Administrator, delete accounts, or change passwords. Each actual change is saved with a separate administrative audit containing actor/target identifiers, the controlled action, and server UTC time. Repeating the same action makes no further change or audit.

Staff membership and security-stamp changes share the audit transaction. After a change, the account holder should sign out and back in to refresh their role claims. Rotating the security stamp invalidates older cookies when Identity next validates them; the current-database membership checks block removed Staff access on the next protected request without waiting for that interval. Apply `AddAdministrativeAuditEvents` before using this page. Its historical identifiers have no account or listing foreign keys, so audit history is retained independently.

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
- [x] Two-account Week 6 browser walkthrough, mobile layout, keyboard/focus, real-time expiration, replacement, and stale cancellation verified in Chrome on October 28; see the browser record below.

The implemented student workflow has passed the automated checks and the October 28 Chrome browser walkthrough below. Official pickup hours are still **unconfirmed**, so the shipped empty schedule disables new reservations. Staff pickup handling is now implemented in Week 7; operational monitoring/repair procedures and real image upload remain future work.

### Week 7 final verification — November 4, 2026

`dotnet restore LUThrift.slnx` passed. `dotnet build LUThrift.slnx` passed with **0 warnings and 0 errors**. The full suite, run with both dedicated PostgreSQL connections and `dotnet test LUThrift.slnx --no-build`, passed **743 tests, 0 failed, 0 skipped**. This includes **40 reservation and 3 catalog PostgreSQL cases** on a disposable PostgreSQL **18.6** instance. The safe EF check reported no pending model changes, and `git diff --check` passed. All temporary reservation schemas were removed, and the disposable container was stopped. The development database, local secrets, and existing migrations were unchanged.

This checkpoint added nine cases: seven PostgreSQL cases and two HTTP workflows. Lock-wait tests now cover both the owner and listing lock with time advancing strictly past the deadline, as well as the existing exact-deadline cases. New races pit pickup against expiration before, at, and after the deadline. Stale pickup and missing-item requests are tested after an actual replacement reservation, following either cancellation or expiration of the original. The two-staff race now checks that the audit actor is the winning staff member. HTTP tests create two students' holds through the real Reserve form, then follow staff lookup through pickup or missing-item reporting, private history/count updates, public hiding, and a stale student cancellation. Database snapshots prove public, staff, and account GETs do not write.

All six required PostgreSQL scenarios ran with committed fixtures, separate connections/DbContexts, synchronized starts, and observed lock waits: two staff collecting one hold; pickup versus student cancellation; pickup waiting beyond its deadline; pickup versus expiration; pickup versus missing-item reporting; and stale staff actions after a replacement hold. Assertions check final reservation/listing states and audit counts, actors, and timestamps. Existing tests also cover revoked/inactive staff, student privacy, antiforgery, forged fields, invalid states, duplicate requests, and audit-failure rollback. No production-code fix or migration was needed.

- [x] Current Staff/Administrator eligibility and private student information verified.
- [x] Strict deadlines, correct-hold checks, server fulfillment time, and audit actor verified.
- [x] Atomic fulfillment and missing-item transitions, rollback, and repeat safety verified.
- [x] Student history/counts, public hiding, and read-only GETs verified.
- [x] All six PostgreSQL race scenarios, migrations/constraint, and required automated checks passed.
- [x] Week 7 browser walkthrough: completed on November 11 in isolated Chrome at 375, 768, and 1440px. Collection, missing items, stale forms after cancellation/replacement, history, search/pagination, current-account access, and keyboard checks passed. See the [actual results and limits](docs/week8-browser-verification.md); the expiration-based replacement variant was not repeated in this browser run.

The November 4 automated results above are supplemented by the November 11 browser walkthrough. Official hours and physical identity-check procedures remain unconfirmed. A named operator, alert/escalation process for failed expiration passes, and repair procedure for inconsistent or orphan holds are still needed; logging and retries do not establish who monitors them. No late-pickup override or role-management screen has been added. Listing photos are now implemented and covered by the Week 8 record.

### Week 8 final verification

On **November 12, 2026**, the review fixed the above-cap photo-upload feedback found in the November 9/11 walkthrough. The status page now recognizes rejected photo POSTs and provides JPEG/PNG, 5 MiB, refresh, and return-to-listing guidance without rereading the body or changing limits, antiforgery, authorization, rate policies, or reservation behavior. Other errors retain their normal messages.

| Check actually run | Result |
| --- | --- |
| `dotnet build LUThrift.slnx` (including restore), then `dotnet build LUThrift.slnx --no-restore` after a test assertion correction | Both passed; **0 warnings, 0 errors** |
| Focused upload/error/image-migration run | Initially **40 passed, 1 failed, 0 skipped**: the new restrictive-delete test expected the wrong SQLSTATE. Correcting the assertion to PostgreSQL's `23001` required no migration change; all four migration cases passed on rerun. |
| Full `dotnet test LUThrift.slnx --no-build` run, executed **once** with both guarded PostgreSQL connections | **883 passed, 0 failed, 0 skipped**; **44 reservation/image-migration and 3 catalog PostgreSQL cases** included |
| Actual `AddListingImages` PostgreSQL migration | Passed: upgrade preserves committed inventory/reservation/audit data; column definitions, both unique indexes, restrictive deletion, orphan rejection, optional photos, and repeated migration checked |
| `dotnet tool restore` and the documented safe EF pending-model-change command below | Passed; **no changes since the last migration** |
| `git diff --check` | Passed |

The test server was a fresh loopback-only, tmpfs PostgreSQL **18.6** container using `luthrift_reservation_test` and `luthrift_catalog_test`. Each reservation/migration case owned its random schema; competing requests used the existing separate-connection test infrastructure. No development database, user secrets, environment configuration, reservation rules, or existing migrations were changed. Ten cases were added: six HTTP rejection/isolation cases and four PostgreSQL image-migration cases. Existing image codec, rollback, permission, rate-limit, cookie/header, safe-error, readiness-failure, and reservation/pickup tests ran in the full suite.

A separate Development Kestrel host on HTTPS port 7143 used `luthrift_upload_test` in that disposable container, synthetic Staff accounts, and temporary image storage. A valid PNG was saved. Then 7 MiB uploads with Content-Length and chunked transfer each returned **400 with the new guidance**, while stored photo hashes and full listing/reservation/image/audit snapshots remained unchanged. These were HTTP probes, **not a fresh browser walkthrough**; certificate verification was bypassed only for this isolated localhost probe. Initial temporary-client issues (newline/message assumptions, early response handling, and cookie transfer) were corrected before these successful observations.

All reservation/migration test schemas were removed. The temporary host and PostgreSQL container were stopped, and probe cookies, payloads, scripts, and photo files were removed. Only non-secret test results were retained locally.

The [November 9/11 browser results](docs/week8-browser-verification.md) remain the recorded layout/keyboard/pickup evidence. The user confirmed **no additional browser checks** on November 12. A fresh browser check of the new above-cap error page remains pending, as do physical devices, other browsers, and screen readers. Native image processing ran on macOS; Linux/Windows runtime checks, deployment storage/backup restoration, multi-instance/proxy rate limits, and PostgreSQL-specific competing photo updates remain unverified. The automated image replacement race tests use SQLite; PostgreSQL migration checks do not substitute for that race coverage.

### Checking the EF model

To compare the EF model with the migration snapshot without connecting to the development database, restore the local tool and use a placeholder connection in the Testing environment:

```sh
dotnet tool restore
ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=1;Database=luthrift_model_check;Username=model_check;Timeout=1' \
  dotnet ef migrations has-pending-model-changes --project src/LUThrift.Web --no-build -- --environment Testing
```

The custom account workflow includes registration, login, logout, access denial, and viewing/canceling owned reservations. Email delivery, campus sign-in, account deactivation, and profile editing remain future work. The Identity UI package still supplies its other standard pages; their presence does not mean email delivery or those application features have been completed.

### Week 6 browser verification — October 28, 2026

The browser review used **Chrome 156.0.8078.48**, running headless through its debugging protocol, with separate cookie contexts for two synthetic Students and signed-out browsing. The app used the documented **Development HTTPS** address, `https://localhost:7131`, with all existing migrations applied to a separate disposable **PostgreSQL 18.6** database. One existing test Student also received Administrator through the Development bootstrap to create fixtures. The development database, user secrets, tracked environment configuration, system clock, and one-hour reservation policy were unchanged. Open-hours checks used explicitly synthetic **00:00–23:59** intervals; these are not official pickup hours.

The completed browser checks are:

- Each Student saw only their own reservations, including when owner query parameters were supplied. Forged other-owner and unknown cancellation IDs returned the same message, and missing antiforgery tokens returned HTTP 400.
- Three reservations succeeded; a fourth was refused. Canceling the intended reservation changed the count from three to two, released that item, and kept Canceled history. Another reservation could then fill the slot. Repeated cancellation and stale availability submissions returned safe feedback.
- Signed-out browsing stayed public; sign-in returned to the selected item without reserving it. Draft details returned 404. Student-only access to Staff/Admin was denied. A staff-created item appeared after publishing, disappeared after Mark missing, and was archived through the Staff form.
- Closed hours, less than one hour remaining, and invalid/unconfigured hours each hid Reserve and rejected a direct POST with a useful message. Cancellation still worked while pickup was closed.
- Screenshots and measured page bounds at **375px, 768px, and 1440px** showed no horizontal overflow. A 150-character title, long HTML-like description, missing size, placeholder image, current/history cards, and campus deadline labels remained readable. Listing markup was displayed as text. Actual Tab/Enter input exercised the mobile menu, Reserve/Cancel focus, and My reservations navigation. Focus rings remained visible after CSS transitions.
- Bootstrap, site/scoped CSS, JavaScript, and the placeholder image returned HTTP 200. The local development certificate was bypassed only in the isolated test browser, without changing system trust. Database snapshots before and after public/account GETs and sign-in return were identical for listings, reservations, and audits.

The real-time expiration check used a reservation created at **04:30:05.366858 America/Chicago**, with its original deadline of **05:30:05.366858** (10:30:05.366858 UTC), exactly one hour later. At 10:30:02.754 UTC, the browser showed it as Active in Current, with Cancel available and a count of three. At 10:30:05.828 UTC—about **0.46 seconds after the deadline**—a fresh GET showed Expired history, no Cancel control, a count of two, and an Available public item. The saved deadline and campus-time label did not change. These are measured browser observations around the boundary, not a claim that a browser refresh occurred at the exact database tick.

The worker recorded the original expiration, listing release, and one audit at **10:30:32.960941 UTC**, about **27.6 seconds after the deadline**, while new reservations were disabled by the synthetic invalid-hours configuration. This confirms the visible deadline did not wait for persisted cleanup. After the walkthrough resumed and synthetic open hours were restored, Student B reserved the released item at **11:11:56.744690 campus time**, receiving a new saved deadline exactly one hour later. Replaying Student A's original saved cancellation returned “This reservation is no longer active.” PostgreSQL snapshots before and after that request were identical: the old record remained Expired, B's replacement remained Active, the listing stayed Held, and no duplicate audit was added. B's refreshed browser retained the same replacement deadline and only B's history.

For cleanup, the replacement was canceled through B's normal form and all 11 disposable listings were archived through Staff actions. The seven reservation records and 39 audits were retained in the test database until teardown; historical titles remained readable even after public details returned 404. The test host, isolated Chrome instance, and disposable database container were then stopped, and the temporary browser profile, synthetic credentials, and saved form tokens were removed. The existing development database and other running application/browser instances were left alone.

The October 28 restore and build passed with **0 warnings and 0 errors**. The full suite passed **514 tests, 0 failed, 0 skipped**, including **19 reservation and 3 catalog PostgreSQL checks** in separate disposable databases. The safe EF model check reported no pending changes, and `git diff --check` passed. No application-code fix was needed. These are browser checks at emulated viewport sizes, not physical-device, Safari/Firefox, or screen-reader coverage. At that checkpoint, staff pickup confirmation and real image upload were still future work; see Week 7 above for the current pickup implementation. Official pickup hours remain **unconfirmed**.

### Repeatable Week 6 browser walkthrough

Use the documented Development HTTPS setup against a separate local test database with migrations applied. Configure explicitly synthetic pickup intervals through local configuration for the walkthrough, with enough time for the complete hour; do not present them as official hours or commit them. Keep the one-hour policy and system clock unchanged.

1. Register two synthetic accounts, A and B, and use separate browser profiles/private windows so their cookies stay separate. Both should receive Student. For fixture setup, the optional development bootstrap can temporarily add Administrator to A's existing account; restart and sign in again. A retains Student. Create and publish at least five clearly labeled test items through Staff inventory, including one with a long title and missing size. Leave another item Draft and confirm it stays hidden.
2. Signed out, open `/Thrift` and Available details. **Sign in to reserve** should return to that item without creating a reservation. As A, reserve one item and check the redirect to My reservations, confirmation, title, count **1 of 3**, and saved deadline with campus timezone and UTC offset. Refresh: no duplicate or later deadline should appear.
3. As B, the same item should show Reserved without A's identity. My reservations should show only B's records. Reserve a different item as B and verify A's page does not show it. With B's own valid Cancel form token, changing the submitted ID to A's reservation must give the same safe response as an unknown ID, leaving both holds unchanged. A missing token must reject the POST.
4. As A, reserve until the count is **3 of 3**; a fourth must be refused. Cancel one through its form. Check **2 of 3**, retained Canceled history, and the released item's Available status. Retry the old Cancel submission: no second change. Reserve another item successfully. Reposting an old Reserve form must not duplicate the hold or extend its deadline.
5. Leave a reservation until its actual displayed deadline. Refresh My reservations just before and at/after that time: it must leave Current, show Expired in history, lose its Cancel control, and stop consuming the limit. Check that the item becomes Available and B can reserve it. A's stale Cancel request must not release B's new hold. Worker cleanup normally follows within a polling pass; the exact boundary is already covered by controlled-time tests and does not depend on catching a browser refresh at an exact tick.
6. Keep an active reservation, then restart with an empty or closed synthetic schedule. New creation must be unavailable while browsing, cancellation, and eventual expiration still work. Also check a window with less than one hour remaining. Restore only the intended local settings afterward. For stale availability, open a form as A and let B reserve the item before A submits; A must receive safe feedback.
7. Repeat details, My reservations, cancellation messages, and navigation at about **375px, 768px, and desktop width**. Check long titles and timezone labels for overflow, readable status/deadline text, keyboard access to all controls, visible focus, and an understandable Cancel label. Confirm Student B cannot open Staff/Admin pages, and successful Staff lifecycle actions still affect the public catalog for unheld items.

Record the browser/version, widths, results, and any defects before marking this walkthrough complete. Retain canceled/expired history; archive eligible test inventory after holds finish rather than deleting listings. Staff pickup actions have their own Week 7 walkthrough below.

### Week 7 browser walkthrough

The following procedure was completed in the November 11 walkthrough, using cancellation for the replacement-hold case. Results, screenshots inspected, and unperformed variants are recorded in [Week 8 photos and Week 7 pickup browser verification](docs/week8-browser-verification.md).

Use a separate disposable local PostgreSQL database with the existing migrations applied, and the documented **Development HTTPS** launch profile. Override `ConnectionStrings__DefaultConnection` only for that local run; do not point these checks at the development database. Supply explicitly synthetic pickup intervals through process configuration, with at least a full hour remaining. Set `Reservations__WeeklyPickupIntervals__0__Day` to today's campus weekday and use `Reservations__WeeklyPickupIntervals__0__OpensAt` and `Reservations__WeeklyPickupIntervals__0__ClosesAt` for a synthetic open span. Keep the real clock and one-hour duration unchanged. These are test hours, not confirmed operating hours.

1. Register two synthetic Students (A and B) and a separate operator account; choose local passwords and keep them out of Git. For an Administrator walkthrough, use the existing Development bootstrap on the operator account. For the Staff-only case, a trusted operator can assign Staff in the **disposable database only** after registration, as shown below. Sign out and back in after changing roles. Use separate browser profiles for the three accounts.
2. As Staff, create and publish three clearly labeled items, including a long title and missing size. As A, reserve two; as B, reserve the third. Check each student's My reservations shows only their own records. Signed out and as a Student, `/Staff/Pickups` must redirect to Login or deny access without exposing student names/emails. Public Reserved cards must not identify owners.
3. As Staff, open Pickups, search A's email, then the exact reservation ID. Check the item, student, ID, statuses, and campus deadline together before pressing **Confirm pickup**. Confirm the redirect and success message, Fulfilled/Collected statuses, and absence of action forms on the completed record. Refresh: no repeat collection. A's history must show the saved collection time, reduce the count by one, and retain the title. B's history stays unchanged. The item must disappear from all public filters and its details must return 404.
4. On A's second active hold, use **Report item missing**. Verify Canceled/Missing, a reduced active count, and the controlled explanation in A's history. Public results and details must exclude it. Replaying the saved staff form or A's old Cancel form must not relist it or add another transition. No temporary Available state should be introduced by this action.
5. For a stale form, open B's active pickup in the Staff browser and leave it open. Cancel as B, then reserve the released item as A. Submit the old Staff confirmation; it must fail safely. Repeat with a saved old Report missing form. A's replacement must remain Active with the same deadline and Held listing; B's old history remains Canceled. The same protection after expiration is covered by PostgreSQL tests; a manual repetition must wait for the actual saved deadline, without changing the clock or policy.
6. At about **375px, 768px, and desktop width**, check the Staff cards/search/results/messages and both students' history. Look for overflow with long titles, emails, and timezone labels. Use Tab/Shift+Tab/Enter to reach search, clear, pagination (if enough fixtures), Confirm pickup, Report item missing, and navigation; check labels, visible focus, and that each action is clearly associated with its item/student. Confirm Bootstrap/site CSS, JavaScript, and placeholder requests succeed in Development.
7. Record browser/version, widths, each actual result, and any defects before marking these checks complete. Cancel remaining test holds and archive eligible inventory through the app; retain reservation/audit history. Collected inventory remains terminal. Stop only this test host/browser and remove only the disposable test database when finished. Do not remove shared development data.

Example Staff-only fixture provisioning, run through `psql` connected to the isolated browser database after registering the **synthetic** address `pickup-staff@example.invalid`:

```sql
INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
SELECT u."Id", r."Id"
FROM "AspNetUsers" u CROSS JOIN "AspNetRoles" r
WHERE u."NormalizedEmail" = 'PICKUP-STAFF@EXAMPLE.INVALID' AND r."Name" = 'Staff'
ON CONFLICT DO NOTHING;
```

This is local test provisioning, not a public role-assignment feature. The official physical identity-check procedure remains pending staff agreement; the walkthrough checks the displayed record without inventing an ID-document requirement.

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
- [Request protection and rate-limit configuration](docs/request-protection.md)
- [Safe errors, logging, and health checks](docs/operational-safeguards.md)
- [Photo and staff pickup browser verification](docs/week8-browser-verification.md)

Repository name: THRIFTLU.
