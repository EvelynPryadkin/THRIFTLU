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

Real image upload is not implemented; inventory and catalog pages use a local SVG placeholder until a later checkpoint. Reservations, reservation pickup confirmation, and automatic expiration remain Week 6 work. Held is reserved for that reservation workflow and cannot be edited or changed by these Week 4 actions. Collected and Archived are terminal for this workflow. There is no audit-history screen or staff-role management screen yet. Pickup hours, room procedures, campus sign-in, hosting, and maintenance are **To Be Confirmed**.

### Public catalog

Signed-out visitors and signed-in users can browse `/Thrift` and open `/Thrift/Details/{id}`. Cards show the title, category, condition, optional size, availability, and placeholder image. Details also show the description. Missing sizes say **Not specified**, and listing text uses Razor's normal HTML encoding. Details show Held as **Reserved**, without identifying who reserved it. Unknown IDs and Draft, Missing, Collected, or Archived details return HTTP 404.

The GET filter form combines case-insensitive title substring search, category, size, and availability. **Available** is the default; **Reserved** means Held; **All catalog items** means Available plus Held only. Categories come from the database, and size choices come from publicly visible listings. Search text is trimmed and treats `%`, `_`, and `!` literally. PostgreSQL uses `ILIKE`; SQLite verification alone is not treated as proof of PostgreSQL search behavior.

Search and size are limited to 150 and 50 characters. Malformed filter values show validation errors with HTTP 400; valid but unmatched choices show a no-results message. Filter selections stay visible and are stored in the URL. Clear filters restores the default catalog.

Pages contain at most 12 items, with the filtered total and current page shown above the cards. Database ordering is newest `CreatedAtUtc` first, then highest `Id` to resolve ties. The `pageNumber` parameter selects a page; missing, malformed, nonpositive, or outside-32-bit-integer values use page 1. Valid numbers beyond the last page use the last page. Empty results show page 1 of 1. Page size is fixed on the server.

Previous/Next links preserve the filters, submitting new filters resets to page 1, and an item's Back to catalog link restores its filters and page. Ordering is stable for unchanged results; staff updates between requests can shift items between pages.

The request flow is browser GET → Razor PageModel → `CatalogService` → EF Core → PostgreSQL. The service applies public visibility and all filters before counting or fetching a page with `Skip` and `Take`. It projects only display fields with no tracking. Browsing does not save listings, reservations, or audit events, and there is no reservation action in this catalog yet.

## Running locally

You'll need the .NET 10 SDK and Docker. On a fresh checkout, copy .env.example to .env and choose a local database password. Set the same password in the web project's user secrets, replacing YOUR_LOCAL_PASSWORD below:

```sh
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=127.0.0.1;Port=5433;Database=luthrift;Username=luthrift;Password=YOUR_LOCAL_PASSWORD" \
  --project src/LUThrift.Web
```

Then start the database, apply the existing migrations (including `AddInventoryAuditEvents`), and run the app:

```sh
docker compose up -d --wait
dotnet tool restore
dotnet ef database update --project src/LUThrift.Web -- --environment Development
dotnet run --project src/LUThrift.Web --launch-profile https
```

Open https://localhost:7131. The local development certificate may need to be trusted in your browser. The automated checks below run independently of this local database.

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
| `/Account` | Signed-in users |
| `/Staff` | Staff or Administrator |
| `/Admin` | Administrator |

These rules cover pages in nested folders too. Anonymous visitors to protected pages are redirected to Login. Signed-in users without the required role are redirected to Access Denied, which returns HTTP 403. Staff and Admin navigation links follow the user's roles, but the server checks access even when someone enters a URL directly.

The home page links to the working public catalog and item details. Staff has a landing page and working inventory dashboard, Create, and Edit pages. Admin has a minimal landing page. Application-specific account/history, pickup, and administration placeholders remain future work. The folder rules will apply as those pages are implemented. Reservation ownership checks remain Week 6 work.

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

### Checking the EF model

To compare the EF model with the migration snapshot without connecting to the development database, restore the local tool and use a placeholder connection in the Testing environment:

```sh
dotnet tool restore
ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=1;Database=luthrift_model_check;Username=model_check;Timeout=1' \
  dotnet ef migrations has-pending-model-changes --project src/LUThrift.Web --no-build -- --environment Testing
```

The custom account workflow is limited to registration, login, logout, and access denial. Email delivery, campus sign-in, account deactivation, and application-specific profile/history pages remain future work. The Identity UI package still supplies its other standard pages; their presence does not mean email delivery or those application features have been completed.

### Manual Week 4 check

Use the local Development setup above and disposable inventory records:

1. While signed out, visit `/Staff/Inventory` and confirm it sends you to Login. Register a development account through `/Identity/Account/Register`. That ordinary Student account should receive Access Denied when opening inventory, Create, or Edit directly.
2. Follow **Optional development administrator** above: store the existing account's email in `DevelopmentAdmin:Email`, restart in Development, then log out and back in. Confirm Staff navigation opens inventory. Remove the optional setting after assignment; the role stays assigned.
3. Open Create. Submit missing required fields and an overlong title with client validation bypassed; confirm errors and category options remain. Submit valid fields and confirm a Draft listing and a success message. Edit it and confirm the status stays Draft. Check the image placeholder and status filter.
4. Publish that listing, edit it while Available, mark it missing, edit it while Missing, then archive it. Confirm each status on the dashboard. Use separate listings to check Available → Archived, Draft → Archived, and Available → Collected through **Record walk-in collection**.
5. Confirm Archived and Collected records remain listed but have no Edit or lifecycle buttons. Refresh after a successful POST and confirm it does not repeat the action. The automated tests check forged requests and Held protection; there is deliberately no UI action that creates a Held listing.
6. Inspect `AuditEvents` with a local database viewer to confirm the listing, actor ID, action, UTC time, and transition details. There is no audit-history page yet. Keep the account email and all local credentials out of Git.

### Week 5 browser checklist

Use disposable inventory records and the existing local Development setup. Browser checks are separate from HTTP tests; unchecked items below remain pending until performed in a browser.

- [ ] At approximately **375px**, **768px**, and a desktop width, inspect the home page, catalog, filtered results, and details. Check card layout, long titles/descriptions, missing sizes, placeholder images, and horizontal overflow.
- [ ] Use only the keyboard to open the mobile navigation, reach Browse items, operate every filter, submit, clear, follow Previous/Next, open details, and return. Check visible focus, labels, selected values, and disabled pagination controls.
- [ ] Repeat browsing signed out and as an ordinary Student. Confirm Student access to Staff/Admin URLs is denied and signed-out access redirects to Login.
- [ ] Combine title, category, size, and availability filters with at least 25 matching temporary items. Browse three pages, open details from page 2, and return to the same selections and page. Submit different filters and confirm page 1. Check empty results and malformed/unknown detail URLs.
- [ ] As Staff or Administrator, create a temporary **Draft** and confirm it is absent from the catalog and returns 404 by direct detail URL. Publish it and confirm it appears. Using separate published items, mark one **Missing**, record one walk-in **Collected**, and **Archive** another; confirm each disappears and its detail URL returns 404. Keep these records archived rather than deleting them.
- [ ] Confirm Held appears as Reserved only in the appropriate filter and direct public details, with no owner information. Use a prepared test fixture for Held; there is intentionally no staff action to place an item on hold.

### Week 5 verification record

On **October 20, 2026**, `dotnet restore LUThrift.slnx` passed, `dotnet build LUThrift.slnx` finished with **0 warnings and 0 errors**, and the complete `dotnet test LUThrift.slnx --no-build` run passed **277 tests, 0 failed, 0 skipped**, with `LUTHRIFT_CATALOG_TEST_CONNECTION` pointing to a disposable local PostgreSQL 16 database. This includes both PostgreSQL tests as well as the isolated SQLite and HTTP tests. The documented EF model check reported **no changes since the last migration**.

`git diff --check` passed. Chrome reached the temporary Testing host, but its CSS and JavaScript assets were not served correctly, so the browser checklist above remains **pending**, not passed. It needs a repeat run with working static assets.

Week 5 acceptance:

- [x] Anonymous and Student catalog/details access; Staff and Administrator protections retained.
- [x] Public visibility, encoded listing text, missing sizes, empty results, and invalid-route behavior covered by passing tests.
- [x] Combined search/filters, pagination, and details/back navigation covered by passing SQLite/HTTP and PostgreSQL checks.
- [x] Inventory, audit, and reservation snapshots unchanged by browsing; staff lifecycle visibility covered by passing HTTP tests.
- [x] Restore, build, complete test suite, whitespace check, and safe pending-model-change check passed.
- [ ] Responsive layouts, keyboard navigation, visible focus, overflow, and the browser lifecycle walkthrough completed with working assets.

Week 5's automated acceptance checks are complete; browser acceptance is still pending. Details are recorded in [the development log](docs/development-log.md). Reservations remain Week 6 work; real image upload remains a later checkpoint.

## Documentation

- [Requirements](docs/requirements.md)
- [Reservation rules](docs/reservation-rules.md)
- [Development log](docs/development-log.md)

Repository name: THRIFTLU.
