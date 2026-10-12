# Allen Xenia Office

A new ASP.NET Core MVC application for Allen Building. This repository is a ground-up implementation of the agreed PRD; the previous application is reference material, not its source tree.

## Development

Requires .NET SDK 10.0.401 (or an allowed servicing patch) and Node 22.22.3. SQLite is the local database; production uses the existing Forge MySQL database with dedicated application credentials. MySQL-specific behavior is verified against a real isolated MySQL instance before release.

```sh
npm ci --ignore-scripts
npm run build:css
dotnet tool restore
dotnet restore
dotnet run --project src/AllenBuilding.Web -- --migrate
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet run --project src/AllenBuilding.Web
```

The application includes invitation-only accounts, challenged manager MFA, recovery, persistent protected keys, private maintenance requests and separate SQLite/MySQL migrations. External delivery and later PRD phases are still being built. No production readiness is claimed. Local migrations create an empty database, not default tenant credentials. Never use real tenants or payments in test fixtures.

After migrating an empty local database, create its first manager interactively:

```sh
dotnet run --project src/AllenBuilding.Web -- --bootstrap-manager
```

The command takes a hidden password, refuses to bootstrap once any account exists, and grants access only to the initial building. Sign in, enroll an authenticator, sign in again with a real challenge, and generate recovery codes before management opens. Manager MFA cannot be bypassed with an enabled flag or remembered browser. Sensitive changes require password and factor verification in the preceding five minutes. Every protected request checks the current security stamp and current building grants.

Managers can add suites and record invitations for new accounts. Acceptance consumes a seven-day Identity token and grants exactly the recorded suite in one transaction. Account tokens are read from URL fragments into antiforgery-protected forms so they do not enter request URLs. Password reset tokens expire after two hours; recovery never grants membership. Invitation, reset and security messages are encrypted in a durable queue; **production email delivery is not connected yet**. Existing-account suite grants, expired-invitation reissue and operator-assisted lost-device recovery are later increments. A valid recovery-code sign-in can reset a lost authenticator; it revokes sessions and requires enrollment again. Recovery codes are hashed, single use and require replenishment before manager authority returns.

Local protected keys live in ignored `.data/keys` with a restricted directory. Production requires a persistent `Protection__KeyDirectory`, a private wrapping `Protection__CertificatePath` and any `Protection__CertificatePassword` via restricted configuration. Preserve the key directory and wrapping certificate independently of releases and test their recovery. Host-specific certificate provisioning and off-host backup/restore remain release gates. Account rate limits currently partition by the direct peer IP; configure and verify trusted reverse-proxy forwarding before production, never trust arbitrary forwarded headers.

## Maintenance milestone

An active tenant can submit, read and comment on their original membership's requests. A different user sharing the suite cannot read them; a returning grant cannot restore access to an ended grant's records. Managers use explicitly separate building-scoped queries and must complete an MFA challenge. They can triage, assign to themselves, start, complete, separately verify/close, cancel, reopen and add tenant-visible or internal comments. Assignment to another manager/contractor and contractor records remain planned.

Shared application actions enforce the domain state machine. Each successful change commits the request, history, encrypted notification messages and an actor-scoped replay receipt together. Exact operation/payload replay returns the committed result; changed operation reuse or stale versions returns a conflict. Shared user locks block security-stamp changes; membership/manager/resource write locks serialize admission without conflicting with recipient foreign-key checks. MySQL uses short READ COMMITTED transactions; SQLite uses immediate write transactions. Access revocation commits locally without contacting billing or email providers, retains existing records, and does not terminate rent consent.

## Notification delivery

The development profile uses a local inbox and runs at `http://localhost:5081`. To process up to twenty due messages:

```sh
dotnet run --project src/AllenBuilding.Web -- --dispatch-mail-once
```

Messages are written as private JSON files in ignored `src/AllenBuilding.Web/.data/mail`. An invitation/reset link uses a URL fragment; open it in the local browser to test activation. No messages are sent to external recipients. Set `Notifications__Enabled=true` locally to run the five-second background dispatcher; it is off by default. Change `Notifications__PublicOrigin` if changing the local port. Production refuses the local-inbox transport and cannot enable an unconfigured dispatcher.

Messages have two-minute leases, fencing tokens, stable message IDs, thirty-second delivery deadlines, exponential backoff and at most five attempts per approved retry cycle. A stale worker cannot mark a reclaimed message delivered. Definite transient errors retry; unknown/permanent outcomes enter Needs review. Delivery checks current original-membership/building access and current comment visibility; expired invitations/reset messages and ended grants are suppressed. Local writes are atomic and idempotent by message ID. Email is at-least-once delivery; the production adapter must define its provider's idempotency/uncertain-result behavior.

The manager's Notification delivery page shows bounded, building-scoped delivery status without tokens or request text. Retrying Needs review requires recent password/MFA, a current message version and an audit reason; resolve the delivery uncertainty/configuration first. Real provider selection, provider delivery evidence and host mail configuration remain outstanding.

## Architecture and authority

Domain contains business rules without framework/provider dependencies. Application holds shared authorized use cases. Infrastructure implements EF/Identity and external adapters. Web composes them and renders MVC views. Pages, APIs and the later assistant use the same application rules.

Canonical editable documentation lives in the owner's vault:

- [PRD](/Users/jeremyvaught/markdown/dotnet/Development/allen-xenia-office-prd-v2.md)
- [Technical specification](/Users/jeremyvaught/markdown/dotnet/Development/allen-xenia-office-technical-spec-v2.md)
- [Project brief](/Users/jeremyvaught/markdown/dotnet/Development/allen-xenia-office-project-brief.md)

These absolute links are local references, not files supplied by a public clone. This README supplies reproducible repository instructions. Planned features are not represented as delivered.

## Commit and release discipline

Commit each coherent, verified increment: foundation, domain rules, persistence/migrations, account flows, complete maintenance workflows, billing, reports, assistant and operational recovery. A milestone includes relevant checks and a descriptive message. Preserve the history; do not combine the rebuild into one final commit. Publication/deployment require their own evidence.

## Database checks and production configuration

```sh
./scripts/verify-mysql.sh
```

The script creates and removes its own loopback-only MySQL 8.4 container. It verifies real migrations, original membership/request ownership constraints, unique active grants, transaction rollback and optimistic concurrency. Default local test runs report this provider check as skipped; the script and CI execute it separately.

Production requires `Database__Provider=MySql` and `ConnectionStrings__Office` from restricted configuration/environment. SQLite is rejected in Production. Use a dedicated database and separate runtime/migration users with the documented privileges; migration credentials must not remain in the web process. Confirm the actual server version, InnoDB, connection/resource limits and off-host restore before deployment. The temporary container does not establish TTMH-5 readiness. Database credentials never belong in this README or tracked appsettings.

Apply migrations through the explicit `--migrate` operator command after a recoverable backup. Web startup does not change the schema. Provider migration histories are separate; do not apply SQLite migrations to MySQL or run both stores as writable sources.

## Frontend and browser verification

The shared MVC layout uses compiled Tailwind v4 and semantic light/dark tokens. System is the initial preference; explicit Light/Dark is retained on the browser and System removes the override. Live enrollment remains pending while delivery and pilot operations are completed.

```sh
npx playwright install chromium
npm run test:browser
```

Browser tests cover System/OS changes, explicit preference across reloads, CSS delivery and a full tenant/manager maintenance workflow at 320px in both themes. They create their own synthetic SQLite database, protected keys and accounts under ignored `artifacts/browser-fixture`, and start an isolated server on loopback port 5088. No test-only enrollment route exists in the application. Screenshots live under `artifacts/browser`. Emulation is not evidence of a real iPhone/Android acceptance run. `npm run dev:css` watches styles during development; rebuild CSS before .NET build/publish so static-asset manifests include it.

The build pins `@parcel/watcher` 2.6.0 through an npm override to remove the unpatched `braces` dependency found during the initial audit. Recheck CSS compilation and watch behavior when changing that pin.
