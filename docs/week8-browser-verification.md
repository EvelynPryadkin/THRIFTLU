# Week 8 photos and Week 7 pickup browser verification

Photo checks began on **November 9, 2026**; the walkthrough continued on **November 11, 2026**. The checks used **Chrome 156.0.8078.81**, driven through Playwright in an isolated headless session. Screenshots were inspected at emulated widths of **375, 768, and 1440 pixels**. These are browser checks, not physical-device or screen-reader testing.

The app ran in Development on HTTPS port 7141 with normal browser certificate validation. A separate disposable PostgreSQL **18.6 (Debian)** container followed the README's loopback-only setup, with the existing migrations applied. Photo storage was temporary and outside the repository. Two Students, a Staff operator, and an Administrator registered through the browser using synthetic accounts. Operator roles were provisioned only in the disposable database.

Pickup hours were explicitly synthetic process settings: Monday, then Wednesday, 00:00–23:59 in America/Chicago. The real clock and one-hour reservation duration were unchanged. The development database, user secrets, and normal browser sessions were not used for these checks.

## Completed checks

| Check | Result and evidence |
| --- | --- |
| Upload and replace a valid PNG | **Passed.** Staff Edit displayed the uploaded photo. Replacing it changed the image bytes and preview; the image response used `no-store` caching. |
| Remove a photo | **Passed.** The placeholder returned in Staff Edit, catalog cards, and details. The former image URL returned 404. |
| Invalid image content | **Passed.** A file named as a JPEG but containing text produced “Choose a complete, valid JPEG or PNG photo.” The existing image ID, served bytes, and preview remained unchanged. |
| Oversized upload | **Passed.** A file of 5 MiB + 2048 bytes, below the 6 MiB request cap, produced “Choose a photo no larger than 5 MiB.” The previous image ID, bytes, and preview remained unchanged. |
| Upload above the request cap | **Feedback check failed.** A 7 MiB PNG submitted through Administrator Edit returned HTTP 400 with the generic “Request could not be completed” page instead of photo-size guidance. The previous photo hash and complete database snapshot remained unchanged. |
| Private photo access | **Passed.** Staff and Administrator could preview a Draft photo. Anonymous and Student image requests returned 404; public Draft details also returned 404. |
| Public photo access | **Passed.** Available and Held items displayed photos without revealing reservation-owner information. Held listing editing returned 409. |
| Photo layouts and controls | **Passed at all three widths.** Staff Edit, public catalog, and details showed decoded photos with suitable alternative text, no horizontal overflow, long-title wrapping, and a missing-size label. Upload controls had labels and visible keyboard focus. |
| Navigation and assets | **Passed.** Tab and Enter operated the mobile navigation toggle and its expanded state. Bootstrap/site CSS, JavaScript, jQuery, and placeholder requests succeeded. |
| Two-Student reservations | **Passed.** A reserved two photographed items and B reserved a third. Each saw only their own records, with counts of two and one. Saved deadlines were exactly one hour after creation. |
| Staff access and lookup | **Passed.** Anonymous requests led to Login; Students received Access Denied without student information. Staff and Administrator opened Pickups. Email and exact-ID filters worked together and retained their selections. |
| Current Staff eligibility | **Passed.** Making the synthetic Staff account inactive, then separately removing its Staff role while retaining the existing cookie, each produced 403 Access Denied without student information and 404 for its Draft photo request. Restoring fixture permissions restored access. |
| Pickup pagination | **Passed.** Twenty-one additional holds were seeded in the disposable database, one per distinct synthetic Student with a one-hour deadline. The browser showed pages of 20 and 1 without duplicates. Tab/Enter operated Next/Previous and preserved the email filter; Clear reset both search fields. Long fixture emails wrapped at 375px, and browsing left the database snapshot unchanged. |
| Pickup and history layouts | **Passed at all three widths.** Long titles, controls, and America/Chicago deadlines with UTC-06:00 labels remained readable without horizontal overflow. Tab/Shift+Tab reached pickup, missing-item, search, and cancellation controls with visible focus. |
| Confirm pickup | **Passed.** Keyboard submission produced Fulfilled/Collected, a saved server fulfillment time, and one staff-attributed audit. A's count dropped and history displayed the saved collection time; B's history was unchanged. The item disappeared publicly, and its details and image returned 404. |
| Repeat pickup | **Passed.** Refreshing and replaying the completed pickup preserved the first fulfillment time and added no audit. |
| Report a reserved item missing | **Passed.** The result was Canceled/Missing with one staff-attributed audit, a reduced Student count, and the controlled explanation in owned history. Public results, details, and image access hid the item. |
| Repeat missing/cancellation | **Passed.** Replaying the missing report and the Student's old Cancel form left inventory Missing and added no transition audit. |
| Replacement-hold protection | **Passed.** B canceled a hold and A reserved the released item. Submitting the original Staff pickup and missing-item forms, then B's old Cancel form, left A's replacement Active/Held with the same deadline and no extra audit. |
| Updated history | **Passed at all three widths.** Collected, missing, canceled, and replacement records remained understandable and visible only to their owner. |
| Read-only browsing | **Passed.** Database snapshots of listings, reservations, image metadata, and audits were identical before and after the public, staff, account, layout, and keyboard GET checks. |
| Administrator pickup actions | **Passed.** The Administrator completed the 21 pagination fixtures through the pickup UI, creating 21 corresponding audits. |
| Photo storage cleanup | **Passed.** Before disposal, the four stored photo files matched the four metadata rows exactly; replaced files had not been left behind. |
| Walkthrough cleanup | **Completed.** The replacement hold was canceled through the Student form. The three eligible Missing/Available/Draft walkthrough items were archived through inventory forms; Collected items stayed terminal. The isolated host, browser, database, photo storage, fixtures, and temporary tools were then removed. |

One feedback defect was found: files above the 6 MiB request cap receive a generic error rather than the useful photo-size message. Failed-upload preservation still passed. This was a verification-only run; no application-code fix was made.

## Failures, limitations, and unverified coverage

| Item | Status |
| --- | --- |
| Staff inventory table thumbnails | **Existing UI limitation.** The inventory table still shows its temporary placeholder even when a photo exists. The uploaded staff preview is on Edit; catalog and details display the actual photo. |
| Above-cap upload feedback | **Failed; unresolved.** The 7 MiB case needs photo-size guidance while retaining the current HTTP rejection and preservation of the previous photo. |
| Expiration followed by a replacement hold | **Not repeated in this browser run.** The stale-form scenario here used cancellation. Existing Week 6 browser and PostgreSQL evidence remains separate; the clock and duration were not changed to accelerate expiration. |
| Other environments | Physical devices, other browsers, and screen readers were not checked in this walkthrough. |

Official pickup hours, physical identity-check procedures, and responsibility for cleanup monitoring remain unconfirmed. No late-pickup override was introduced. Credentials and saved form tokens are not part of this report or Git.

## Cleanup record

Immediately before disposing of the isolated database, it contained **29 listings, 25 reservation records, 46 audits, and four image metadata rows**, with zero Active reservations and zero Held listings. History and audits remained intact until the disposable database was removed.

Only this walkthrough's browser, host on port 7141, and disposable container were stopped. The container was confirmed absent afterward. Temporary image files, upload fixtures, browser tools, and package cache were removed. Non-secret screenshots and result summaries were retained locally under `/private/tmp/luthrift-browser.srQdHO/evidence`.

The normal app on port 7131 still returned Healthy. The development database, existing development-log edit, and untracked JDK directory were left unchanged.

## Focused automated verification

The following existing regression checks ran on November 11 and passed **85 tests, zero failures, zero skips**:

```sh
dotnet test LUThrift.slnx --no-build --filter 'FullyQualifiedName~ListingImageHttpTests|FullyQualifiedName~StaffPickupConfirmationTests|FullyQualifiedName~StaffPickupWorkflowTests|FullyQualifiedName~ReservedItemMissingTests' --logger 'console;verbosity=minimal'
```

This command used the existing isolated test infrastructure, not the development database. It did not run a build or the full suite. The full suite remains reserved for Prompt 5.
