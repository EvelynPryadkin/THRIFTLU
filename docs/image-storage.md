# Listing photo storage — Week 8, checkpoint 1

Each listing can have zero or one photo. This checkpoint adds storage, validation, metadata, and service operations only. There are no upload forms or image-serving routes yet; existing pages keep the placeholder. The database migration is `AddListingImages`.

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

This checkpoint does not apply it to the development database automatically. Back up image storage together with the database: metadata alone cannot restore the photo bytes. Changing `RootPath` requires moving the existing files as well. Local disk is suitable for a single host, or hosts sharing the same storage directory; independent disks on multiple application instances are not supported.

## Accepted content and dependency

- The upload limit is **5 MiB (5,242,880 bytes)**, measured from actual stream bytes, including non-seekable streams. Browser filenames, MIME types, and reported lengths do not determine acceptance.
- Only actual **JPEG and PNG** content is accepted. The entire image must decode successfully; incomplete, unsupported, corrupt, or animated input is rejected.
- Before allocating decoded bitmaps, dimensions must be positive, no side may exceed **8192 pixels**, and the image must contain at most **20,000,000 decoded pixels**. These server limits are fixed for this checkpoint, separate from the compressed upload limit.
- All eight EXIF orientation values are normalized into the pixels. A fresh image is encoded as JPEG (quality 90) or PNG, preserving PNG transparency. Uploaded EXIF, GPS, XMP, comments, and source color profiles are not copied; pixel colors are converted to sRGB. Re-encoded file length can differ from the upload length and is recorded in metadata.

The added dependency is [SkiaSharp 4.154.0](https://www.nuget.org/packages/SkiaSharp/4.154.0), a maintained cross-platform .NET binding to Skia, under the [MIT license](https://github.com/mono/SkiaSharp/blob/main/LICENSE.txt). Its package supplies macOS/Windows native assets; `SkiaSharp.NativeAssets.Linux.NoDependencies` at the same version supplies the Linux runtime without a fontconfig dependency. Preserve the packages' bundled license and third-party notices when distributing the application. The processor uses raster codecs, without `System.Drawing` or platform-specific image commands.

## Service and database behavior

Future staff-authorized PageModels call `ImageService`; the authenticated actor ID must come from server authentication, matching `ListingService`. This API does not itself create an HTTP route or grant permission. UI authorization and antiforgery integration belong to Prompt 2.

`AddOrReplaceAsync(listingId, upload, actorUserId, expectedImageId)` adds when `expectedImageId` is null and no photo exists. To replace, supply the image ID read with the edit page. `RemoveAsync(listingId, expectedImageId, actorUserId)` requires that same current ID. Each successful replacement has a fresh server-generated GUID and filename. A stale ID, including a repeated removal, returns Conflict and asks for a refresh. Neither client filenames nor path strings are accepted by these methods.

The metadata row records its ID, listing ID, generated storage filename, detected content type, re-encoded byte length, dimensions, and server UTC creation time. A unique listing relationship enforces at most one photo; restrictive deletion prevents a listing deletion from silently leaving its image file behind. Existing reservation relationships and rules are unchanged.

Images can change only on Draft, Available, or Missing listings, following existing edit rules. The service takes the listing write lock using a conditional, same-value status update, then reads and checks the expected image ID. Concurrent requests therefore serialize before checking the photo. Held, Collected, and Archived records remain protected. No image operation subsequently acquires an owner lock, so it does not invert reservation owner → listing coordination. Photo changes never alter listing status or reservation records.

A replacement is decoded and written to a new immutable private file before opening the database transaction. In that transaction the service checks current state, swaps metadata, and saves a staff-attributed `ListingImageAdded`, `ListingImageReplaced`, or `ListingImageRemoved` audit. It owns the transaction and rejects calls inside an existing one. The old file is deleted only after a confirmed commit. A rejected operation or known rollback removes only its new candidate file and leaves the old metadata/file intact. Failed pending entities are detached so a later `SaveChanges` cannot retry them accidentally.

The filesystem and database do not share a distributed transaction. If commit acknowledgement is lost, the service preserves both files and returns an unconfirmed result; refresh before retrying. A crash or failed file cleanup can leave an unreferenced file. Cleanup failure after a successful commit is logged safely and returned as `CleanupPending`; it does not undo a saved photo. Cleanup failure after a rejected operation is logged while retaining that operation's original failure result. Cleanup uses only generated filename shapes and ignores request cancellation after commit. Operator reconciliation must compare files with committed metadata while image writers are stopped, retain all referenced files, and investigate uncertain operations before deleting anything. Automatic orphan cleanup is outside this checkpoint.

## Focused verification

Run the image and database-model tests without running the full suite:

```sh
dotnet test LUThrift.slnx --filter 'FullyQualifiedName~ImageUploadTests|FullyQualifiedName~ListingImageProcessorTests|FullyQualifiedName~DatabaseModelTests'
git diff --check
```

Tests use generated synthetic image bytes, SQLite, and isolated temporary directories. They do not read user secrets or use the development database. The full suite, browser/UI checks, and deployment-specific storage checks are reserved for later prompts.

Checkpoint verification on November 5, 2026: dependency restore succeeded; the solution build finished with **0 warnings and 0 errors**; the focused command above passed **64 tests, 0 failed, 0 skipped**. Coverage includes valid/corrupt/spoofed content, byte and pixel limits, all eight orientations, metadata removal, optional/unique metadata, add/replace/remove, stale and simultaneous replacements on separate SQLite connections, audit-failure rollback, cancellation before commit, uncertain commit acknowledgement, and failed cleanup after success. Listing and reservation snapshots remain unchanged by photo operations.

The safe placeholder-connection EF check reported no pending model changes. PostgreSQL SQL was generated and inspected: it creates only the new table, restrictive foreign key, and unique indexes. This migration was **not applied to PostgreSQL** during this checkpoint, and image concurrency was exercised on SQLite rather than PostgreSQL. `git diff --check` passed. Native decoding was exercised on macOS; Linux and Windows execution remain unverified. No browser automation or full-suite run was performed.
