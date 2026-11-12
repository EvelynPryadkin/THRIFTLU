# Listing photos — Week 8

Each listing can have zero or one photo. Checkpoint 1 added storage, validation, metadata, and service operations; checkpoint 2 connects them to staff editing and public catalog/details pages. The existing database migration is `AddListingImages`; checkpoint 2 needs no additional migration.

## Local configuration on macOS

Choose a private, application-owned directory outside the checkout and `wwwroot`. From the repository root:

```sh
mkdir -p "$HOME/Library/Application Support/LUThrift/listing-images"
chmod 700 "$HOME/Library/Application Support/LUThrift/listing-images"
dotnet user-secrets set "ImageStorage:RootPath" \
  "$HOME/Library/Application Support/LUThrift/listing-images" \
  --project src/LUThrift.Web
```

Do not place files or the machine-specific path in Git. Missing or invalid configuration makes image writes unavailable without preventing browsing or application startup. The service requires an absolute path, resolves existing symlink components, and rejects paths within the checkout, application content root, or web root, as well as storage directories that contain the checkout. Keep the directory under the application's control; do not let untrusted local users replace its directories or symlinks.

Apply the migration to the intended local database using the existing setup command:

```sh
dotnet ef database update --project src/LUThrift.Web -- --environment Development
```

The app does not apply it to the development database automatically. Back up image storage together with the database: metadata alone cannot restore the photo bytes. Changing `RootPath` requires moving the existing files as well. Storage must survive app restarts and deployment replacement; container deployments need a persistent mounted volume, not a disposable writable layer. Local disk is suitable for a single host, or hosts sharing the same storage directory; independent disks on multiple application instances are not supported.

## Accepted content and dependency

- The upload limit is **5 MiB (5,242,880 bytes)**, measured from actual stream bytes, including non-seekable streams. Browser filenames, MIME types, and reported lengths do not determine acceptance.
- Only actual **JPEG and PNG** content is accepted. The entire image must decode successfully; incomplete, unsupported, corrupt, or animated input is rejected.
- Before allocating decoded bitmaps, dimensions must be positive, no side may exceed **8192 pixels**, and the image must contain at most **20,000,000 decoded pixels**. These server limits are fixed for this checkpoint, separate from the compressed upload limit.
- All eight EXIF orientation values are normalized into the pixels. A fresh image is encoded as JPEG (quality 90) or PNG, preserving PNG transparency. Uploaded EXIF, GPS, XMP, comments, and source color profiles are not copied; pixel colors are converted to sRGB. Re-encoded file length can differ from the upload length and is recorded in metadata.

The added dependency is [SkiaSharp 4.154.0](https://www.nuget.org/packages/SkiaSharp/4.154.0), a maintained cross-platform .NET binding to Skia, under the [MIT license](https://github.com/mono/SkiaSharp/blob/main/LICENSE.txt). Its package supplies macOS/Windows native assets; `SkiaSharp.NativeAssets.Linux.NoDependencies` at the same version supplies the Linux runtime without a fontconfig dependency. Preserve the packages' bundled license and third-party notices when distributing the application. The processor uses raster codecs, without `System.Drawing` or platform-specific image commands.

## Service and database behavior

Staff-authorized PageModels pass their authenticated `User` to `ImageService`. The service derives the actor ID from that principal and verifies the account exists, is active, and currently has Staff or Administrator membership in the database. Cookie role claims alone cannot authorize changes. Checks run before processing uploads, after acquiring the listing lock, and around saving, so a revoked or inactive account cannot rely on an old form. The service does not accept an actor ID or role from form data.

`AddOrReplaceAsync(listingId, upload, user, expectedImageId)` adds when `expectedImageId` is null and no photo exists. To replace, supply the image ID read with the edit page. `RemoveAsync(listingId, expectedImageId, user)` requires that same current ID. Each successful replacement has a fresh server-generated GUID and filename. A stale ID, including a repeated removal, returns Conflict and asks for a refresh. Neither client filenames nor path strings are accepted by these methods. `GetForEditAsync` checks current staff eligibility and returns only the current image ID; it does not require configured file storage to permit ordinary listing editing.

The metadata row records its ID, listing ID, generated storage filename, detected content type, re-encoded byte length, dimensions, and server UTC creation time. A unique listing relationship enforces at most one photo; restrictive deletion prevents a listing deletion from silently leaving its image file behind. Existing reservation relationships and rules are unchanged.

Images can change only on Draft, Available, or Missing listings, following existing edit rules. The service takes the listing write lock using a conditional, same-value status update, then reads and checks the expected image ID. Concurrent requests therefore serialize before checking the photo. Held, Collected, and Archived records remain protected. No image operation subsequently acquires an owner lock, so it does not invert reservation owner → listing coordination. Photo changes never alter listing status or reservation records.

A replacement is decoded and written to a new immutable private file before opening the database transaction. In that transaction the service checks current state, swaps metadata, and saves a staff-attributed `ListingImageAdded`, `ListingImageReplaced`, or `ListingImageRemoved` audit. It owns the transaction and rejects calls inside an existing one. The old file is deleted only after a confirmed commit. A rejected operation or known rollback removes only its new candidate file and leaves the old metadata/file intact. Failed pending entities are detached so a later `SaveChanges` cannot retry them accidentally.

The filesystem and database do not share a distributed transaction. If commit acknowledgement is lost, the service preserves both files and returns an unconfirmed result; refresh before retrying. A crash or failed file cleanup can leave an unreferenced file. Cleanup failure after a successful commit is logged safely and returned as `CleanupPending`; it does not undo a saved photo. Cleanup failure after a rejected operation is logged while retaining that operation's original failure result. Cleanup uses only generated filename shapes and ignores request cancellation after commit. Operator reconciliation must compare files with committed metadata while image writers are stopped, retain all referenced files, and investigate uncertain operations before deleting anything. Automatic orphan cleanup is outside this checkpoint.

## Staff editing and image delivery

Create the listing first, then open `/Staff/Inventory/Edit?id=LISTING_ID`. The photo section has an uploaded preview or the existing placeholder. Upload/replace and remove use separate antiforgery-protected POST forms under the existing Staff folder policy. Only the listing-information handler binds `UpdateListingInput`; photo handlers bind the uploaded file and expected image ID. This keeps missing listing fields from invalidating photo-only submissions and prevents submitted listing/status/actor fields from changing inventory.

Success redirects back to Edit with a confirmation. Validation errors reload current metadata, categories, and preview; stale forms return HTTP 409 with a refresh message. The page caps requests and multipart bodies at 6 MiB to bound parsing overhead, while the service's actual image-byte limit remains 5 MiB. A rejected upload that reaches the application's 400/413 status page receives JPEG/PNG, 5 MiB, refresh, and return-to-listing guidance without reading its body again. This guidance does not claim every rejection is a size error; missing antiforgery or malformed forms can also cause 400. An upstream proxy can enforce its own limit before this app receives the request. The browser's `accept` hint is a convenience; decoded content remains authoritative. There is no upload field on the Create page in this checkpoint.

Catalog summaries/details project only `HasImage` in addition to their existing display fields. Uploaded images use `/Thrift/Image/{listingId}` with responsive contained sizing and encoded alternative text derived from the title. Listings without metadata keep the local SVG placeholder. Storage filenames and filesystem paths never appear in these URLs or view models.

The image endpoint calls `ImageService.OpenReadAsync` on every request. Anonymous visitors and Students may read photos only for Available or Held listings, matching public catalog/details visibility. Current active Staff/Administrators may also preview Draft or Missing listings, following edit eligibility. Collected and Archived photos remain inaccessible to everyone through this endpoint. Unknown listings, absent photos, missing files, invalid storage configuration, and unauthorized private requests return 404 without exposing filesystem details. The service rechecks visibility/current role membership after opening the file and never writes during a read.

Image responses use `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`, including endpoint 404s. They supply no ETag, Last-Modified, or download filename. Publishing/removing a listing therefore changes access at the same image URL on the next request; old conditional-cache headers cannot bypass the visibility check. A file that is missing despite existing metadata is an operational storage problem and returns 404, rather than serving an unrelated file.

## Focused verification

Run the image and database-model tests without running the full suite:

```sh
dotnet test LUThrift.slnx --filter 'FullyQualifiedName~ImageUploadTests|FullyQualifiedName~ListingImageProcessorTests|FullyQualifiedName~DatabaseModelTests'
git diff --check
```

Tests use generated synthetic image bytes, SQLite, and isolated temporary directories. They do not read user secrets or use the development database. The checkpoint results below are historical; see the [November 12 final verification](../README.md#week-8-final-verification) for the full suite and real PostgreSQL image-migration checks, and the [November 9/11 browser record](week8-browser-verification.md) for layout, keyboard, and workflow observations. Deployment-specific storage checks remain pending.

Checkpoint 1 verification on November 5, 2026: dependency restore succeeded; the solution build finished with **0 warnings and 0 errors**; the focused command above passed **64 tests, 0 failed, 0 skipped** at that checkpoint. Coverage includes valid/corrupt/spoofed content, byte and pixel limits, all eight orientations, metadata removal, optional/unique metadata, add/replace/remove, stale and simultaneous replacements on separate SQLite connections, audit-failure rollback, cancellation before commit, uncertain commit acknowledgement, and failed cleanup after success. Listing and reservation snapshots remain unchanged by photo operations.

The safe placeholder-connection EF check reported no pending model changes. PostgreSQL SQL was generated and inspected: it creates only the new table, restrictive foreign key, and unique indexes. This migration was **not applied to PostgreSQL** during this checkpoint, and image concurrency was exercised on SQLite rather than PostgreSQL. `git diff --check` passed. Native decoding was exercised on macOS; Linux and Windows execution remain unverified. No browser automation or full-suite run was performed.

Checkpoint 2 verification: the final build passed with **0 warnings and 0 errors**. The image HTTP/storage, staff listing form, catalog service, and public catalog page run passed **96 tests**; a separate public-details and expired-hold catalog run passed **28 tests**. Both runs had **0 failures and 0 skips**. The new coverage checks Staff/Administrator upload/replace/removal, anonymous/Student denial, current database eligibility despite stale cookies, missing antiforgery tokens, forged fields, invalid uploads, stale forms, read-only image GETs, every public/private status, missing files, safe response headers, and visibility changes at an unchanged URL. The review fixed photo-only forms incorrectly validating empty listing fields; the existing listing form tests still pass.

```sh
dotnet test LUThrift.slnx --no-restore --filter 'FullyQualifiedName~ListingImageHttpTests|FullyQualifiedName~ImageUploadTests|FullyQualifiedName~StaffListingFormTests|FullyQualifiedName~CatalogServiceTests|FullyQualifiedName~PublicCatalogPageTests'
dotnet test LUThrift.slnx --no-build --filter 'FullyQualifiedName~PublicItemDetailsTests|FullyQualifiedName~CatalogExpirationTests'
```

No schema or dependency change was needed for checkpoint 2. At that point the full suite was reserved for Prompt 5, and PostgreSQL runtime and browser layout/keyboard checks had not been performed. Later results are linked above. The November 12 full suite passed 883 tests without skips, including four new PostgreSQL image-migration cases; the existing simultaneous image replacement tests still use SQLite. Linux/Windows native runtime, real deployment storage/backup restoration, and PostgreSQL-specific photo update races remain unverified.
