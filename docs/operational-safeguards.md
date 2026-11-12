# Errors, logging, and health checks — Week 8

## Safe error responses

Unhandled request failures render a friendly `/Error` page with HTTP **500**, a server-generated request reference, and Home/Browse links. The reference matches the application log's `RequestId`. The page does not show exception messages, stack traces, submitted fields, or the failed URL. If a submitted change might have succeeded, the message advises checking its current status before retrying.

Empty error responses re-execute `/Status/{statusCode}` without redirecting the browser. A missing route, hidden item, or unknown item renders a useful **404** page; other error codes remain unchanged. Existing Login and Access Denied redirects still work, and responses already containing validation feedback are not replaced. The error/status pages only render content, including when the original request was POST. Their antiforgery exemption does not authorize a write operation. Both pages are anonymous, use no-store caching, and use a minimal layout without account/database queries so a database outage cannot break their navigation.

Rejected Staff Edit photo POSTs that reach this status handler with 400 or 413 receive JPEG/PNG, 5 MiB, refresh, and return-to-listing guidance. The handler recognizes the original server re-execution route and first upload-handler query value; direct `/Status` query parameters cannot select this message. A bounded positive listing ID may build a navigation link, but cannot load data or bypass the Staff policy. The body is not read again, and no exception, filename, or arbitrary failed URL is displayed. Other 400 responses retain their generic message. A proxy rejection before the request reaches the app remains the proxy's responsibility.

These responses also apply in local Development; use a debugger to inspect exceptions locally. No public error response includes diagnostic details. A response already streaming to the client cannot be replaced by an error page: the connection is aborted and a safe failure event is logged.

## Logs and audit history

The application emits structured events containing safe operational fields:

| Event | ID | Fields |
| --- | --- | --- |
| `RequestCompleted` | 1000 | RequestId, status code, elapsed milliseconds |
| `RequestFailed` | 1001 | RequestId |
| `RequestAborted` | 1002 | RequestId |
| `DatabaseReadinessFailed` | 1003 | RequestId (or `background` outside a request) |

Request events do not record raw URLs/search queries, student emails, headers, cookies, passwords, tokens, form values, uploaded filenames/content, SQL, or connection strings. Exception objects are deliberately excluded. Application messages from inventory, images, Identity registration, and reservations were reviewed and already avoid sensitive exception details; those messages and expiration failure/retry reporting remain enabled. Database audit events and reservation history are unchanged.

ASP.NET Core request diagnostics, EF Core, Npgsql, and framework health-check logging are filtered because those categories can include payloads or exception text. The filter preserves existing application/host log levels and applies even if a more-specific framework category is configured as Trace. Sensitive EF logging and detailed EF errors remain disabled. ASP.NET Core's exception-handler diagnostics are suppressed separately; `RequestFailed` supplies the safe correlation event instead. This deliberately trades framework request/SQL detail for safe operational events. Any future telemetry needs the same exclusions; external proxy/access logs are outside this application's filters.

## Health endpoints

Both endpoints allow anonymous requests, return minimal plain text, use no-store caching, and are outside the sensitive-POST rate policies:

| Endpoint | What it checks | Response |
| --- | --- | --- |
| `/health/live` | The running process can answer HTTP; no database check | `200 Healthy` |
| `/health/ready` | EF Core can connect to the configured database | `200 Healthy`, or `503 Unhealthy` |

Readiness uses a five-second cancellation timeout and never returns exception text, connection settings, dependency names, or diagnostic data. It checks connectivity only: it does not apply migrations, test every schema/permission, inspect image storage, or write inventory, reservations, or audit rows. A passing readiness check **does not prove reservation expiration cleanup is running or succeeding**. Continue monitoring the expiration worker's failure/retry events and overdue holds. Operational ownership for cleanup monitoring remains to be agreed.

The existing role initializer still accesses the database before startup completes. A database outage at cold start can prevent the app from starting; these endpoints report the state of an already-running process, including a later connection failure. This checkpoint does not alter startup role initialization or reservation rules.

## Simple local checks

Use the documented local database setup first, then run:

```sh
dotnet run --project src/LUThrift.Web --launch-profile https
```

In another terminal:

```sh
curl -k -i https://localhost:7131/health/live
curl -k -i https://localhost:7131/health/ready
curl -k -i https://localhost:7131/this-page-does-not-exist
curl -k -i https://localhost:7131/Error
curl -k -I https://localhost:7131/Staff/Pickups
```

Expect 200/200 for healthy probes, 404 for the missing page, 500 for the safe error preview, and a Login redirect for anonymous Staff access. `/Error` renders the template directly; it is not an endpoint for deliberately throwing an exception. `-k` is only for the local development certificate; use normal certificate verification for deployment probes. Probe HTTPS directly so an HTTP-to-HTTPS redirect is not mistaken for a health result.

Readiness failure and unexpected GET/POST exceptions are exercised with isolated test databases and controlled faults. Do not interrupt the shared development database just to test failure responses. The November 8 checkpoint did not run browser automation or the full suite; the later [November 12 verification](../README.md#week-8-final-verification) includes all operational tests.

## Verification record — November 8, 2026

- `dotnet build LUThrift.slnx --no-restore`: passed with zero warnings and errors.
- Focused HTTP and worker regression run: **173 passed, zero failed, zero skipped**. This includes 11 new operational cases plus request protection, account/authorization, reservation, pickup, image, and expiration-worker checks.
- `git diff --check`: passed.

The new operational cases can be run on their own with:

```sh
dotnet test LUThrift.slnx --no-build --filter 'FullyQualifiedName~OperationalSafeguardsTests'
```

The tests verified Production error responses, real request correlation, sensitive-value exclusion even with verbose framework settings, authentication redirects, antiforgery 400 responses, database-independent error rendering, and 503 readiness with working liveness. The readiness-failure fixture uses a synthetic unavailable SQLite file connection because SQLite considers an in-memory database connectable without opening a file. No development database was changed. The local curl commands above, browser checks, and PostgreSQL outage behavior were not exercised in this checkpoint.
