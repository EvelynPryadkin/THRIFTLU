# Email confirmation and campus accounts

New student registrations accept only the exact domain `lawrence.edu`, compared without case sensitivity. Personal domains, subdomains, and addresses that only contain that domain as part of another domain are rejected. Identity still normalizes email and enforces uniqueness. Registration never accepts a role, confirmation flag, account identifier, or timestamp from the browser.

## Registration and confirmation

The Register PageModel commits the account and Student membership in one database transaction. New accounts have `EmailConfirmationRequiredForSignIn = true`; registration does not issue an authentication cookie. After the commit, `EmailConfirmationService` asks Identity for an email-confirmation token and sends it through `IEmailSender`. An email-delivery failure leaves the account intact and directs the user to request another link.

Links are valid for 24 hours. They are built from validated `EmailConfirmation:PublicBaseUrl`, never the incoming Host or forwarded-host headers. Use an HTTPS origin with no credentials, path, query, or fragment. Development also accepts loopback HTTP. A missing origin disables delivery; an invalid nonblank origin fails startup with a safe configuration message. Return URLs must be local.

`/Identity/Account/ConfirmEmail` validates the token with Identity and redirects to a URL without the token or account ID before showing success or failure. It uses `no-store` and `no-referrer`, and does not sign the user in automatically. `/Identity/Account/RegisterConfirmation` never displays a token or debug confirmation link. `/Identity/Account/ResendEmailConfirmation` accepts an antiforgery-protected POST and gives the same generic response for unknown, inactive, confirmed, undeliverable, and eligible accounts. Resend defaults to three POSTs per ten minutes per trusted user ID or connection IP; GETs are unaffected. See [request protection](request-protection.md).

No production provider has been selected. The default sender cannot deliver email. A production adapter must implement `Microsoft.AspNetCore.Identity.UI.Services.IEmailSender` and replace its registration, with credentials supplied through deployment secrets. It must not log recipients, message bodies, callback URLs, or provider errors containing secrets. Configure persistent, protected ASP.NET Core Data Protection keys across restarts and shared keys across replicas so existing links remain usable. Configure upstream request logging to exclude confirmation query strings too. See [Microsoft's Identity confirmation guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/accconfirm?view=aspnetcore-10.0).

## Local development without an email provider

The opt-in Development sender writes HTML messages to a private directory outside the repository and `wwwroot`. It uses the same Identity tokens and confirmation endpoint as normal delivery; it never sets `EmailConfirmed` directly. No special confirmation bypass exists in Production. The sender checks Development itself, and Production registration ignores the pickup-directory setting.

For macOS, from the repository root:

```sh
mkdir -p "$HOME/Library/Application Support/THRIFTLU/confirmation-mail"
chmod 700 "$HOME/Library/Application Support/THRIFTLU/confirmation-mail"
dotnet user-secrets set "EmailConfirmation:PublicBaseUrl" "https://localhost:7131" --project src/LUThrift.Web
dotnet user-secrets set "EmailConfirmation:DevelopmentPickupDirectory" "$HOME/Library/Application Support/THRIFTLU/confirmation-mail" --project src/LUThrift.Web
dotnet run --project src/LUThrift.Web --launch-profile https
```

Apply the migration below first. For test walkthroughs use the README's separate disposable database, not real accounts. Register a uniquely named synthetic `@lawrence.edu` account with a local password. Open the newest HTML file in that private directory, then follow **Confirm email** and sign in. The local sender does not contact a real campus inbox. To test an existing account, use the resend page. Do not paste confirmation URLs into logs, documentation, or issue reports.

Directory permissions must be `0700`; message files use generated names and `0600` permissions. The sender rejects paths inside the checkout/web root and symlink directories. Delete local messages after testing; they contain recipient information and time-limited bearer links. This local sender requires Unix permissions (macOS/Linux); Windows delivery needs another adapter. The sender and production-environment rejection are covered by focused tests, but a manual browser/delivery-provider check remains pending for this checkpoint.

To disable local file delivery:

```sh
dotnet user-secrets remove "EmailConfirmation:DevelopmentPickupDirectory" --project src/LUThrift.Web
```

## Existing-account transition

`AddAccountEmailConfirmationRequirement` adds one Boolean column with a database default of `false`. It does not change `EmailConfirmed`, passwords, roles, reservations, or audit history. New registration explicitly sets the flag to `true` on the server. Identity's custom `IUserConfirmation<ApplicationUser>` allows existing accounts to keep signing in while enforcing confirmation for new accounts. Active-account checks and login lockout still apply.

| Account | Sign-in and existing access | New reservations |
| --- | --- | --- |
| New unconfirmed student | Sign-in denied until confirmed | Denied |
| Existing unconfirmed Student | Sign-in, own history, and eligible cancellation remain | Denied until campus email is confirmed |
| Confirmed campus Student | Normal active-account checks | Eligible subject to hours, availability, and limit |
| Existing Student with a personal email | Existing sign-in/history/cancellation remain | Denied even if that email is confirmed |
| Existing Staff or Administrator | Existing management access remains, subject to current active membership | If also a Student, the same verified campus-email rule applies |

Rollout procedure:

1. Back up the intended database and apply the additive migration through the normal deployment process. Locally, the existing command is `dotnet ef database update --project src/LUThrift.Web -- --environment Development`. Confirm the target connection first; this checkpoint does not apply it to the development database automatically.
2. Configure and verify the production sender, trusted public origin, and persistent Data Protection keys before inviting new registrations. Until then, registration can create an unconfirmed account, but real email delivery remains unavailable. Users can retry through resend after delivery is configured.
3. Tell existing Students to request confirmation for their campus email. Do not automatically mark them confirmed or invalidate their existing reservation history. Creation checks current verified-email state, including after lock waits; cancellation, expiration, and pickup keep their previous rules and deadlines.
4. Existing personal-email accounts remain unchanged. A staff-approved account email correction/linking process is unresolved and outside this checkpoint; do not delete accounts or create duplicate histories to work around it. Existing Staff/Administrator management access is retained.

Official pickup hours, physical identity-check procedures, and cleanup-monitoring ownership remain unconfirmed. Email confirmation proves access to an inbox; it does not invent those operational policies or add campus SSO.

## Verification

Focused tests use SQLite, real Identity tokens/cookies/antiforgery, and a fake `IEmailSender`. No real email is sent. They cover confirmation, invalid/expired links, unconfirmed sign-in denial, campus-domain rejection, forged flags, trusted origins, safe return URLs, resend feedback and limits, delivery failure, existing-account access, and new-reservation eligibility. Separate sender tests use temporary private directories and reject non-Development environments.

The new migration has not been applied to a real account database. Production delivery, deployment key persistence, a browser walkthrough, and PostgreSQL migration execution are not verified in this checkpoint. The full suite remains reserved for Prompt 12; the focused command results are recorded in the completion report.

The solution build passed with **0 warnings and 0 errors**, and the final focused run passed **268 tests, 0 failed, 0 skipped**. One older test initially failed because it expected registration to sign in immediately; it now verifies confirmation followed by protected-cookie login. The successful commands were:

```sh
dotnet build LUThrift.slnx --no-restore --disable-build-servers -m:1
dotnet test LUThrift.slnx --no-build --no-restore --filter 'FullyQualifiedName~EmailConfirmation|FullyQualifiedName~StudentRegistrationTests|FullyQualifiedName~RegistrationSecurityTests|FullyQualifiedName~AccountSecurityTests|FullyQualifiedName~CurrentAccountAuthorizationTests|FullyQualifiedName~RequestRateLimitTests|FullyQualifiedName~RequestSecurityTests|FullyQualifiedName~ReservationServiceTests|FullyQualifiedName~ReservationLifecycleTests|FullyQualifiedName~StudentReserveActionTests|FullyQualifiedName~StaffMissingItemTests|FullyQualifiedName~StaffPickupWorkflowTests|FullyQualifiedName~PersistedPickupHoursTests|FullyQualifiedName~DatabaseModelTests|FullyQualifiedName~RoleInitializerTests' --logger 'console;verbosity=minimal'
```

The README's nonconnecting Testing command reported **no pending EF model changes**. `git diff --check` also passed. No development database or user secrets were modified.
