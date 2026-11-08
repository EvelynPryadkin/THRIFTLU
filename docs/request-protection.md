# Request protection — Week 8

The application uses ASP.NET Core's built-in rate limiter for selected POST requests. Existing account lockout, authorization, ownership checks, local return-URL validation, and Razor Pages antiforgery protection still apply. A request passing the limiter must also pass those checks.

## Default limits and configuration

Each policy has an independent fixed window with no request queue:

| Configuration group | POST requests covered | Requests per window | Window |
| --- | --- | --- | --- |
| `Login` | `/Identity/Account/Login` | 10 | 60 seconds |
| `Registration` | `/Identity/Account/Register` | 5 | 600 seconds |
| `Reservations` | `/Account/Reserve` and `/Account/Reservations` (Cancel), sharing a budget | 20 | 60 seconds |
| `Pickups` | `/Staff/Pickups` (ConfirmPickup and ReportMissing), sharing a budget | 30 | 60 seconds |
| `ImageUploads` | `/Staff/Inventory/Edit?handler=UploadPhoto` | 10 | 60 seconds |

Defaults live in `RequestProtectionOptions`. Override only the values needed through normal ASP.NET Core configuration. For example, a local configuration override can be set from the repository root:

```sh
dotnet user-secrets set "RequestProtection:Login:PermitLimit" "15" --project src/LUThrift.Web
dotnet user-secrets set "RequestProtection:Login:WindowSeconds" "60" --project src/LUThrift.Web
```

Environment variable equivalents are `RequestProtection__Login__PermitLimit` and `RequestProtection__Login__WindowSeconds`. The other groups use the same two property names. Permit limits must be 1–1000 and windows must be 1–3600 seconds; invalid settings fail startup with a configuration error. Restart the application after changing these settings. These defaults are starting limits, not measurements of a production workload.

The key is the authenticated user's ID when available, otherwise the connection's remote IP. Users sharing an IP have independent authenticated budgets; anonymous visitors behind the same NAT share a budget. Missing connection IPs share an `unknown` bucket. IPv4-mapped IPv6 addresses are normalized. Form fields, email addresses, `Forwarded`, and `X-Forwarded-For` cannot choose a key. No forwarded-header trust has been added: if a reverse proxy is deployed, its trusted forwarding setup must be configured explicitly; otherwise anonymous traffic can share the proxy's connection IP budget.

Reservations, pickups, and uploads require authentication before entering their limiter. Login and registration also limit anonymous POSTs. The rate limiter runs after routing, authentication, and authorization, before Razor Pages handlers and antiforgery validation. An authorized POST with a missing token still consumes a permit and is rejected by antiforgery while permits remain; once exhausted, it receives 429 without executing the handler.

A rejected request receives **HTTP 429**, a numeric `Retry-After` header in seconds, `Cache-Control: no-store`, and a short instruction to wait and retry. It is not queued or processed. The app does not automatically replay a form submission.

Policies are attached to Razor Page endpoints through application-model conventions, not attributes on individual handler methods. This covers aliases such as `/Staff/Pickups/Index`. Each policy allows GET and HEAD without a limiter. The image policy checks the same handler selection as Razor Pages (route value, otherwise the first query value, ignoring case); ordinary listing edits and photo removal keep their existing behavior. Extra handler query values or form fields cannot change which upload budget applies.

There is no global limiter. Browsing, image GETs, static files, and unselected endpoints keep their normal behavior. The application currently has no health-check route; this change does not add one.

## Cookies and response headers

All four Identity cookie schemes use `HttpOnly` and `SameSite=Lax`. Outside Development they always use `Secure`, so authentication requires HTTPS. In Development, `SameAsRequest` preserves both documented launch profiles: HTTP on localhost:5278 and HTTPS on localhost:7131. HTTPS requests still receive Secure cookies. Existing cookie names, paths, lifetimes, login/access-denied redirects, and login lockout settings are preserved.

Responses receive:

- `X-Content-Type-Options: nosniff` to prevent content-type guessing.
- `Referrer-Policy: strict-origin-when-cross-origin` to avoid sending a full URL to another origin.
- `X-Frame-Options: DENY` and `Content-Security-Policy: frame-ancestors 'none'` to prevent embedding the application in a frame.

The headers also apply to errors, redirects, static files, and throttled responses. Framing headers remain consistent when antiforgery generates a form response. The CSP currently controls framing only; it does not impose new script or style restrictions. Production HTTPS redirection and HSTS remain unchanged.

## Verification and limits

Focused HTTP tests use isolated SQLite databases, real Identity cookies and antiforgery tokens, and controlled connection IPs supplied only by test middleware. Production code never reads the test IP header. Tests cover rejection and retry guidance, separate user/IP/policy budgets, forwarding-header spoofing, route aliases and handler selection, normal browsing/assets, cookie flags in Production and Development, response headers, and preserved account lockout.

Verification on November 6, 2026: `dotnet build LUThrift.slnx --no-restore` succeeded with zero warnings and errors. The following focused test command passed **155 tests, with zero failures or skips**, including 35 new request-protection cases and affected account, reservation, pickup, and photo regressions:

```sh
dotnet test LUThrift.slnx --no-build --filter 'FullyQualifiedName~RequestSecurityTests|FullyQualifiedName~RequestRateLimitTests|FullyQualifiedName~AccountSecurityTests|FullyQualifiedName~StudentRegistrationTests|FullyQualifiedName~RegistrationSecurityTests|FullyQualifiedName~AuthorizationTests|FullyQualifiedName~StudentReserveActionTests|FullyQualifiedName~MyReservationsPageTests|FullyQualifiedName~StaffPickupConfirmationTests|FullyQualifiedName~StaffPickupWorkflowTests|FullyQualifiedName~ListingImageHttpTests' --logger 'console;verbosity=minimal'
```

Counters are in memory per application instance and reset on restart. Multiple instances do not share budgets; deployment-specific limits, proxy configuration, and load behavior remain unverified. See [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0) for the middleware's deployment considerations. The full suite is reserved for Prompt 5; no browser automation or development database changes are part of this checkpoint.
