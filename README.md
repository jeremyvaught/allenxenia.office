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

Managers can add suites and record invitations for new accounts. Acceptance consumes a seven-day Identity token and grants exactly the recorded suite in one transaction. Account tokens are read from URL fragments into antiforgery-protected forms so they do not enter request URLs. Password reset tokens expire after two hours; recovery never grants membership. Invitation, reset and security messages are encrypted in a durable queue; **production email delivery is not connected yet**. Managers can grant another suite to a confirmed existing account without changing its password or restoring earlier memberships. Grants use actor-scoped replay receipts. Unaccepted invitations can be reissued or revoked with a reviewed version and audit reason; reissue opens a new seven-day window and invalidates old links and queued invitations. Acceptance and invitation changes serialize to prevent a revoked link from granting access. Operator-assisted lost-device recovery is a later increment. A valid recovery-code sign-in can reset a lost authenticator; it revokes sessions and requires enrollment again. Recovery codes are hashed, single use and require replenishment before manager authority returns.

Local protected keys live in ignored `.data/keys` with a restricted directory. Production requires a persistent `Protection__KeyDirectory`, a private wrapping `Protection__CertificatePath` and any `Protection__CertificatePassword` via restricted configuration. Preserve the key directory and wrapping certificate independently of releases and test their recovery. Host-specific certificate provisioning and off-host backup/restore remain release gates. Account rate limits currently partition by the direct peer IP; configure and verify trusted reverse-proxy forwarding before production, never trust arbitrary forwarded headers.

## Maintenance milestone

An active tenant can submit, read and comment on their original membership's requests. A different user sharing the suite cannot read them; a returning grant cannot restore access to an ended grant's records. Managers use explicitly separate building-scoped queries and must complete an MFA challenge. They can record a request for an active tenant through the same submission transaction; the original tenant membership owns the request, and immutable history identifies the manager who recorded it. Eligible tenant choices are building-scoped, bounded and exclude ended grants. Repeated submissions return one accepted record. Revocation prevents new on-behalf submissions, while an authorized manager can recover the receipt of an already accepted operation. They can triage, assign to a building manager or active contractor, start, complete, separately verify/close, cancel, reopen and add tenant-visible or internal comments. Contractor contacts are scoped to management; deactivation prevents new assignment while retaining existing assignments and their accepted history. Stale contact edits and cross-building assignments are rejected.

The manager queue defaults to the oldest unfinished work, including Completed requests awaiting verification. Status, priority, minimum age, assignee and ordering filters run in the scoped database query before paging. Explicit status selection can show closed/cancelled work, while Include closed and cancelled expands an unfiltered status queue. Pages contain at most 50 records, with previous/next links preserving filters and a 1,000-page bound. Stable timestamp/ID ordering handles ties. Age means whole UTC days since original submission, not time in the current reopened cycle. Historical assignments remain filterable after contractor deactivation or manager departure; foreign building names and assignments never enter filter options. Invalid filter values are rejected instead of silently broadening a query.

Shared application actions enforce the domain state machine. Each successful change commits the request, history, encrypted notification messages and an actor-scoped replay receipt together. Exact operation/payload replay returns the committed result; changed operation reuse or stale versions returns a conflict. Shared user locks block security-stamp changes; membership/manager/resource write locks serialize admission without conflicting with recipient foreign-key checks. MySQL uses short READ COMMITTED transactions; SQLite uses immediate write transactions. Access revocation commits locally without contacting billing or email providers, retains existing records, and does not terminate rent consent.

## Maintenance aging report

The manager portal links to `/Reports/Maintenance`, an MFA-protected report with current building scope. Active work means Submitted, Triaged, Assigned or InProgress, split by elapsed time since original submission into under 7 days, 7 to under 30 days, and at least 30 days. Reopening retains that original age. Completed requests awaiting verification have their own group; Closed/Cancelled and future-created records are excluded. The report shows its UTC snapshot time, whole-scope totals and stable 50-record pages with building/age-group filters. A new page or refresh takes a new snapshot; no historical reconstruction is claimed.

`ManagementReportQueries` calls a parameterized SQL reader through DI. Counts and rows share a transaction snapshot: MySQL uses an enforced read-only REPEATABLE READ transaction, SQLite a deferred read transaction. Fresh authority checks discard results if a reported building grant or the current ticket is no longer valid before release. Queries use fixed projections/predicates, bind all values and never accept arbitrary SQL. Interactive reads have a five-second cancellation deadline and command timeouts; a failure shows a retry page without partial totals or database diagnostics.

The shared SQLite/MySQL persistence contract uses 4,010 synthetic report-fixture requests, including 3,000 in an unauthorized building, plus the earlier contract fixtures. It verifies boundary instants, empty populations, tied timestamps, all-page reconciliation, reopened requests, concurrent status changes, revocation during reads and actual MySQL read-only enforcement. After refreshing disposable database statistics it records twenty warmed, sequential full-report timings, nearest-rank p50/p95, dataset distribution, environment metadata, exact parameterized SQL and SQLite `EXPLAIN QUERY PLAN` / MySQL `EXPLAIN ANALYZE` evidence in ignored `artifacts/reports/maintenance-aging-{sqlite,mysql}.json`. Reproduce with the ordinary tests and `./scripts/verify-mysql.sh`. CI is configured to retain these artifacts. Existing suite/maintenance indexes serve these queries; no new migration or before/after optimization gain is claimed. Workstation timings do not establish production latency or capacity.

This milestone covers current maintenance aging. Completion-cycle statistics, financial balance/exception reporting, remote telemetry dashboards and production operating evidence remain later work. Issued charges alone cannot support an accurate outstanding-balance report while settlement and adjustment records are unfinished.

## Billing planning (collection disabled)

Shared application actions and both database schemas now support original-membership rent accounts, calendar terms, explicit one-time obligations and manager-requested rent issuance. The account and calendar period remain stable across term replacements. Reissuing a billed period returns its original obligation; an existing amount/due date is not silently rewritten. Changed terms use a reviewed account version, a recent manager password/MFA challenge and an account lock; overlapping schedules and stale writes are rejected. A partial opening/closing month requires an explicit manager charge. Due day 31 clamps to the last calendar day of shorter months.

Operation receipts and audits commit with each accepted mutation. Provider-specific unique keys enforce one account per original membership, one calendar period identity and one base obligation per period. Composite foreign keys prevent attaching a period or agreement to the wrong account. Revoked portal access cannot erase debt or stop an otherwise valid schedule; a new membership gets a separate account and cannot retrieve the old account.

Development enables `Billing:PlanningEnabled` for manager account opening, calendar terms/replacements, one-time charges and monthly rent issuance. Its default is false outside Development; disabled routes reject reads, preparation and confirmation. Each mutation has a server-rendered review with exact membership/account, amount, dates and effect, followed by a separate antiforgery-protected confirm POST. Five-minute protected tickets belong to the preparing actor, preserve the form's operation identity and canonical command, and cannot be altered through posted amount/date fields. Confirmation uses the shared application use case and rechecks current building authority, recent password/MFA and account version. Repeated preparation/confirmation of the same operation returns one result. Rent previews retrieve the exact original period independently of the bounded latest-record list, including after replacement terms.

Tenants can read only their current original membership's accounts and issued amounts. Management can inspect accounts after access revocation. Record lists are bounded and show their limits. The page explicitly distinguishes issued amount from payment status and outstanding balance.

This is billing planning, **not an enabled payment feature**. `CollectionAvailable` remains false and there are no payment buttons. Stripe customers, collection attempts/items, provider handoffs, settlement/credit/refund/dispute records, reconciliation, autopay, billing notices and financial exception screens still require implementation and provider evidence. Assistant pending actions with durable edit/cancel lifecycle are a separate later increment; these ordinary MVC reviews do not implement that assistant protocol. No application secret was copied from the previous app. Planning never calls Stripe or treats an issued obligation as a payment.

## Building help

Managers write plain-text articles with Public or Active tenants visibility, an editor and a review date. Save draft creates a revision; a separate POST publishes the exact reviewed revision. Editing a published article leaves the previous approved revision visible until publication. Withdrawal removes it from public/tenant reads and search. Draft text never enters the reader/search projection. Tenant guidance requires current access to that building; manager privileges alone do not grant tenant retrieval. The same application retrieval boundary will serve the future assistant.

Article bodies render as escaped text, not executable HTML. Management retains bounded revision history and audited save/publish/withdraw actions. No building hours, contacts or emergency instructions are invented; management must supply and approve those facts. Public/tenant search is bounded to 100 results and 120 characters. MySQL calendar dates use an explicit date conversion verified against the actual provider.

## Notification delivery

The development profile uses a local inbox and runs at `http://localhost:5081`. To process up to twenty due messages:

```sh
dotnet run --project src/AllenBuilding.Web -- --dispatch-mail-once
```

Messages are written as private JSON files in ignored `src/AllenBuilding.Web/.data/mail`. An invitation/reset link uses a URL fragment; open it in the local browser to test activation. No messages are sent to external recipients. Set `Notifications__Enabled=true` locally to run the five-second background dispatcher; it is off by default. Change `Notifications__PublicOrigin` if changing the local port. Production refuses the local-inbox transport and cannot enable an unconfigured dispatcher.

Messages have two-minute leases, fencing tokens, stable message IDs, thirty-second delivery deadlines, exponential backoff and at most five attempts per approved retry cycle. A stale worker cannot mark a reclaimed message delivered. Definite transient errors retry; unknown/permanent outcomes enter Needs review. Delivery checks current original-membership/building access and current comment visibility; expired, revoked or superseded invitations, expired reset messages and ended grants are suppressed. Local writes are atomic and idempotent by message ID. Email is at-least-once delivery; the production adapter must define its provider's idempotency/uncertain-result behavior.

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

## Coverage reports

After building Release and restoring the local tools, run:

```sh
./scripts/coverage.sh
```

The script requires Python 3 and runs the ordinary .NET tests with Coverlet. It merges their OpenCover results with the pinned ReportGenerator tool and checks that all four application assemblies have coverage data. Each invocation creates a fresh ignored `artifacts/coverage/run.*` directory containing raw coverage/TRX results and HTML, Cobertura, JSON and text summaries. `artifacts/coverage/current.txt` identifies the latest run; open its `report/index.html`. Reports are generated after test failures when coverage data is available, and the command retains a failing exit status. CI is configured to upload this evidence even when a check fails; a hosted run has not yet been demonstrated.

This measures application C# and source-mapped Razor exercised by the ordinary domain and SQLite/HTTP integration tests. Test/fixture assemblies, EF migrations, `obj` sources and explicitly coverage-excluded/generated attributes are excluded. Handwritten async methods, lambdas, auto-properties and business rules remain included. The separately executed real MySQL contract and Playwright journeys are outside this coverage measurement; JavaScript, CSS and real-device compatibility need their own checks. Line/branch percentages are improvement goals, not newly introduced release gates. Passing behavioral acceptance scenarios remains required.

## Frontend and browser verification

The shared MVC layout uses compiled Tailwind v4 and semantic light/dark tokens. System is the initial preference; explicit Light/Dark is retained on the browser and System removes the override. Shared dropdown styling keeps native HTML select semantics while normalizing WebKit's compact menu rendering, with a theme-aware decorative indicator and space for its selected text. Browser checks require at least 44px height/width for theme and report selectors. Live enrollment remains pending while delivery and pilot operations are completed.

```sh
npx playwright install chromium webkit
npm run test:browser
```

Browser tests cover System/OS changes, explicit preference across reloads, CSS delivery and a full tenant/manager maintenance workflow, requests recorded on a tenant’s behalf, filtered open/finished management queues, contractor deactivation and draft/public/private/withdrawn help flows, existing-account suite grants and invitation reissue/revocation and reviewed rent-account/terms/charge/period workflows at 320px in both themes. Projects cover desktop Chromium, mobile Chromium and mobile WebKit. The npm command runs WebKit against its own fresh fixture server so the combined sign-in traffic does not exhaust the shared loopback IP account limit. Production rate limits remain unchanged. They create their own synthetic SQLite database, protected keys and accounts under ignored `artifacts/browser-fixture`, and start an isolated server on loopback port 5088. No test-only enrollment route exists in the application. Screenshots live under `artifacts/browser`. Emulation is not evidence of a real iPhone/Android acceptance run. `npm run dev:css` watches styles during development; rebuild CSS before .NET build/publish so static-asset manifests include it.

The build pins `@parcel/watcher` 2.6.0 through an npm override to remove the unpatched `braces` dependency found during the initial audit. Recheck CSS compilation and watch behavior when changing that pin.
