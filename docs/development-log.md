# Development Log

## Week 1: Planning and project setup

I set up the THRIFTLU repository and organized the folders for the website, tests, and documentation. Most of the files were placeholders at this point.

I wrote the project overview, requirements, and reservation rules. The main goal is to let students see what is available in the thrift room before visiting and reserve an item for pickup. I outlined what students, staff, and administrators would need to do, along with the proposed one-hour reservation period and limit of three active reservations per student.

I chose ASP.NET Core Razor Pages with C#, EF Core, PostgreSQL, and xUnit. Pickup hours and the process for holding items still needed to be discussed with thrift-room staff.

## Week 2: Connecting the database

I set up the .NET 10 web and test projects and added them to the solution. I then connected the app to PostgreSQL through EF Core, with the database running in Docker. I used port 5433 because another project was already using 5432. The database data is stored in a Docker volume, the password is in an ignored `.env` file, and the connection string is in user secrets.

I added the user, category, listing, and reservation models and applied the first migration, `InitialDatabase`. I added three categories and four sample items to test the connection. The home page now loads those items and their categories from PostgreSQL.

I also added a test to check that reservations must belong to a user and a listing, and that deleting either cannot automatically erase reservation history.

The build finished without warnings or errors, and the test passed. I checked that all four sample items appeared on the home page and that EF Core reported no pending model changes. The whitespace check passed too. The local HTTPS certificate still needed to be trusted, so the page check bypassed certificate validation.

By the end of Week 2, the database and sample inventory display were working. Sign-in, reservation actions, and staff forms were still to come.

## Week 3: Accounts and page access

I added registration, login, and logout using ASP.NET Core Identity and the existing PostgreSQL tables. Registration asks for a display name, email, password, and password confirmation. The display name is required and limited to 100 characters. The server sets the account's creation time and active status, and Identity handles email normalization and password hashing.

I added Student, Staff, and Administrator roles. The app creates missing roles at startup and checks for existing ones so restarting it does not add duplicates. New accounts receive Student automatically. Creating the account and assigning that role happen in one transaction, so a failed assignment does not leave a partly registered account or sign the user in.

For local development, an email in user secrets can give Administrator access to an account that already exists. This only runs in Development and does not create an account or set a password. After the role changes, the user needs to log out and back in.

I protected the Staff folder for staff and administrators, and the Admin folder for administrators only. Anonymous visitors are sent to Login. Signed-in users without permission see Access Denied. The navigation shows the links each role can use, but entering a protected URL directly still goes through the server's access checks. I added simple Staff and Admin landing pages to check this without building their tools yet.

I added tests that run the app with SQLite in memory, so checking page access does not need Docker or touch the development database. They cover the role access matrix, repeated role setup, bootstrap restrictions, and registration failures. I also added HTTP form tests for attempts to submit a higher role, missing antiforgery tokens, generic login errors, and logout. The original database-model test is still included.

The final restore and build passed with no warnings or errors. All 47 tests passed, and EF Core reported no pending model changes. These checks did not connect to the development database.

The home page remains the working public catalog. The other catalog and private account files are still placeholders, so their tests check the folder rules rather than working pages. Reservation actions, ownership checks, inventory tools, and user administration are still for later weeks. Email confirmation remains disabled because no email service is configured.

## Week 4: Staff inventory

I added the staff inventory dashboard, with all listing statuses, a status filter, and the newest items first. Staff and Administrators can reach it from the Staff landing page. The existing Staff folder policy also protects Create, Edit, and the lifecycle POST handlers.

Create and Edit use separate input models for the title, description, condition, optional size, and category. The PageModels call `ListingService`, which checks the fields and category, trims text, and saves the changes. New listings start as Draft. Editing a Draft, Available, or Missing listing keeps its status. IDs, timestamps, status changes, and audit information do not come from the form.

I added Publish, Mark missing, Record walk-in collection, and Archive. Publish moves Draft to Available. An Available item can become Missing or Collected, and Draft, Available, or Missing items can be archived. The service checks these rules against the database, so changing a form value or using an old page cannot bypass them. Held is reserved for future reservations. Held, Collected, and Archived listings cannot be edited or changed by the Week 4 actions.

The `AddInventoryAuditEvents` migration adds the audit table. Every successful create, edit, or lifecycle action records the listing, signed-in actor, action, and UTC time. Status changes also record the old and new status. Each listing change and its audit are saved in the same transaction. Staff archive records instead of deleting them, and the database prevents deleting a listing with audit history. The actor ID remains historical information even if the account is later removed.

For this final checkpoint, I reviewed the existing tests and added checks for forged roles and audit fields, editing each editable status, and lifecycle access and antiforgery protection across the POST handlers. The tests use SQLite in memory and the existing `WebApplicationFactory` setup. They do not use the development PostgreSQL database.

Real image upload is not implemented; the pages use a temporary local SVG. Reservations, reservation pickup confirmation, expiration, and ownership checks are still future work. There is no audit-history screen or staff-role management screen. I added a manual development-account and inventory checklist to the README; documenting the steps does not mean a manual browser run has been completed.

## Week 5: Public catalog

I added the catalog at `/Thrift` and changed the home page into a simple landing page with a Browse items link. Visitors can browse without an account. The cards show the title, category, condition, size, and availability, and link to a page with the full description. Both pages use the temporary image placeholder.

I kept the public queries in `CatalogService`, separate from the staff listing service. The default catalog shows Available items. The Reserved filter shows Held items, and All catalog items includes those two statuses only. Details use the same public-status restriction, so a direct URL for a Draft, Missing, Collected, Archived, or unknown item returns 404. Held is displayed as Reserved without showing who reserved it. Listing text is HTML-encoded, and a missing size is shown as Not specified.

The search and filter form uses GET so its selections can be bookmarked. Title search ignores case, trims surrounding spaces, and treats characters such as `%` and `_` as text. Category, size, and availability filters work together. Results are counted after filtering and split into pages of 12, ordered by creation time and then ID. Previous, Next, and Back to catalog keep the selected filters and page. Submitting new filters starts at page 1. Invalid page numbers use a safe first or last page; invalid filter values show validation messages.

For the final review, I added tests that follow staff publishing and removing items from public view while a Student browses. I also checked filtered browsing, pagination, details, and return links as both a signed-out visitor and a Student, comparing the database before and after to confirm those reads do not change listings, audits, or reservations. A small accessibility fix gave the mobile navigation container the ID already referenced by its toggle button.

Verification on October 20, 2026: restore passed, the build had 0 warnings and 0 errors, and the complete test run passed all 277 tests with 0 failures and 0 skips. This included both optional PostgreSQL tests in a separate disposable local PostgreSQL 16 database. Those tests inserted their own records in transactions and rolled them back, without resetting the database or deleting existing data. PostgreSQL checks covered case differences, literal search characters, combined filters, and pages of 12, 12, and 1 with tied timestamps. The README's placeholder-connection EF check reported no changes since the last migration. No migration was added.

The whitespace check passed. A Chrome browser attempt reached the temporary Testing host, but that host did not serve its CSS and JavaScript assets correctly. I did not count that attempt as a successful layout or keyboard check.

On October 25, I repeated the browser check using the documented Development HTTPS setup and local PostgreSQL 18.6. The local database needed the existing audit migration applied first. I kept the existing HTTP process running and started the verification host on HTTPS port 7131 only. Bootstrap, site and scoped styles, JavaScript, jQuery, and the placeholder image all returned HTTP 200. The Development setup served them correctly without an application-code change. The local certificate was bypassed only for these isolated checks, without changing system trust.

I used Chrome 155 with viewport sizes of 375, 768, and 1440 pixels, drove the browser with keyboard and mouse input, and inspected the screenshots. The home page, cards, filters, and details fit those widths without horizontal overflow, including a maximum-length title and description and an item without a size. The mobile menu worked. Keyboard navigation reached every filter and the submit, clear, paging, details, and back links, with visible focus. These were emulated browser sizes, not tests on physical phones or every browser.

Signed-out and Student browsing worked, and direct Staff/Admin URLs still required the right access. A combined search and filter check against PostgreSQL returned 25 matching items across pages of 12, 12, and 1. Case differences and literal search characters worked, and opening details from page 2 and returning kept the selections and page. Empty results, invalid URLs and filters, hidden statuses, Reserved display, and encoded listing text also passed. Database snapshots confirmed that browsing did not change listings, audit events, or reservations.

Using a temporary Staff account, I created three Drafts through the browser, published them, and then marked them Missing, Collected, and Archived. Each appeared in the catalog only while Available; its details returned 404 before publishing and after removal. PostgreSQL contained all nine expected audit events. The four original sample listings stayed unchanged. After approved cleanup, the 36 labelled test listings remain Archived or Collected, their audit history is preserved, and both temporary accounts are removed. The prepared Held fixture needed a scoped maintenance query to archive it, with a separate cleanup audit; no staff Held action was added.

The October 25 restore passed, and the build finished with 0 warnings and 0 errors. The default full suite passed 275 tests, with 0 failures and 2 skips out of 277. Those skips were the opt-in PostgreSQL tests, whose dedicated test connection was not set for this run; the browser walkthrough used local PostgreSQL instead. The safe EF model check again found no pending changes, and the whitespace check passed. The README's Week 5 browser checklist is now complete. No application code or migration was added for this review.

Reservations and their ownership checks remain Week 6 work. There is no action here to reserve an item or put it in Held. Real image upload is still for a later checkpoint. Changes to inventory between requests can move items between pages; the ordering tests cover unchanged results.

## Week 6: Student reservations

I added the student reservation flow: reserve an Available item, see it in My reservations, and cancel it before the deadline. Reservations last one hour, and each student can have up to three active reservations. At the exact deadline a reservation is expired, even if the background worker has not updated its stored status yet. Canceled and expired reservations stay in history and no longer count toward the limit.

The pickup schedule comes from configuration. A whole hour must fit inside one continuous open period, without crossing a break or a closed date. Times are stored in UTC and displayed in campus time with the timezone and offset. America/Chicago is the default. Official hours are still unconfirmed, so the app ships with no open intervals and does not allow new reservations until valid hours are configured. Tests use synthetic hours and a controlled clock.

The public details page posts only the listing ID to a protected Account page. The service gets the student ID from the signed-in user and checks the account, role, hours, availability, and limit again. My reservations filters by that same owner before fetching data. It shows all current reservations and the latest 20 history entries; older records stay in the database. Cancel uses a protected POST too. Changing an ID in a form cannot cancel another student's reservation, and GET requests never release holds or write audits.

Reservation changes and their audits are saved together. PostgreSQL locks the student row first and the listing row second. The student lock prevents simultaneous requests for different items from exceeding the limit; the listing lock prevents two students from taking the same item. The `EnforceSingleActiveReservationPerListing` migration adds another safeguard: one stored Active reservation per listing. It reports conflicting existing data instead of deleting it. Expiration and cancellation use the same lock order and check which reservation owns the hold before releasing it. The worker cleans up in bounded batches, including when pickup hours are closed. Creation can also reconcile an expired hold without waiting for the worker.

For the final review on October 27, I added three PostgreSQL race tests and one complete HTTP workflow test. The new race tests cover simultaneous duplicate submissions, cancellation/expiration competing with a replacement at the deadline, and cancellation competing with a new request before the deadline. Each operation has its own connection and transaction. Fixtures are committed first, requests start together, and the tests wait until PostgreSQL reports blocked requests before releasing them. The HTTP test reserves three items, rejects a fourth, cancels to free a slot, and reserves again after expiration while cleanup is delayed. It checks history, counts, deadlines, audits, and unchanged data during browsing. No production-code fix was needed.

Restore passed, the build finished with 0 warnings and 0 errors, and the complete suite passed **514 tests, 0 failed, 0 skipped**. That includes 19 reservation and 3 catalog checks on disposable PostgreSQL 18.6 databases, with real migrations, the unique index, concurrency, and rollback tests. All test schemas were removed and the temporary container was stopped. The development database and local configuration were untouched. The safe EF check found no pending model changes, and `git diff --check` passed. No new migration was added in this final checkpoint.

The two-account browser walkthrough is still pending. I added steps to the README for mobile layout, keyboard focus, deadline display, cancellation, and waiting for real expiration. HTTP tests do not replace those checks. Official pickup hours, staff pickup confirmation, and cleanup monitoring/repair procedures still need work. Real image upload remains a later checkpoint.
