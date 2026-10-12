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

The Week 3 account and authorization foundation and Week 4 staff inventory workflow are implemented on top of PostgreSQL. The home page shows Available listings from the database, including the original sample items.

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

Real image upload is not implemented; the inventory pages use a local SVG placeholder. Reservations, reservation pickup confirmation, and automatic expiration are future work. Held is reserved for that later reservation workflow and cannot be edited or changed by these Week 4 actions. Collected and Archived are terminal for this workflow. There is no audit-history screen or staff-role management screen yet. Pickup hours, room procedures, campus sign-in, hosting, and maintenance are **To Be Confirmed**.

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

The home page is the current simple public catalog. Staff has a landing page and working inventory dashboard, Create, and Edit pages. Admin has a minimal landing page. The other catalog, account, pickup, and administration files are still empty placeholders without routes. The folder rules will apply as those pages are implemented. Reservation ownership checks remain future work.

## Automated checks

The tests require the .NET 10 SDK and restored NuGet packages. They do not require Docker, PostgreSQL, or user secrets:

```sh
dotnet restore LUThrift.slnx
dotnet build LUThrift.slnx
dotnet test LUThrift.slnx
git diff --check
```

The integration tests use `Microsoft.AspNetCore.Mvc.Testing` for .NET 10. `WebApplicationFactory<ApplicationDbContext>` uses the web assembly as its entry point, so `Program` does not need to be exposed. The test host runs in the Testing environment with SQLite in memory and temporary data-protection keys. Route tests use controlled Identity cookies with the normal authentication middleware; form tests submit requests with real antiforgery tokens. These substitutions live only in the test project.

The suite covers the Staff/Admin access matrix, role-dependent navigation, Access Denied responses, registration overposting, login failures, logout protection, role initialization, development bootstrap restrictions, and registration rollback. Week 4 tests cover inventory-page access, dashboard rendering and filtering, server-side validation, unknown categories, Draft creation, edits that preserve status, forged listing/role/audit fields, every allowed and rejected lifecycle transition, and Held protection. They also check audit recording and rollback, stale listing state, missing antiforgery tokens, and requests that try to change state through GET.

The original database-model test is retained alongside audit relationship and deletion tests. Tests for empty account and catalog files check their folder conventions; they do not claim those routes exist. SQLite tests do not replace PostgreSQL-specific verification or a manual browser check.

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

## Documentation

- [Requirements](docs/requirements.md)
- [Reservation rules](docs/reservation-rules.md)
- [Development log](docs/development-log.md)

Repository name: THRIFTLU.
