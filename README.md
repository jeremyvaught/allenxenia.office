# Allen Xenia Office

An ASP.NET Core MVC portal for seven Allen Building tenants. Shared application use cases support private maintenance requests, manager workflow, published help and a staged chatbot. SQLite runs locally; production targets the existing Forge MySQL server.

## Run locally

Requires .NET SDK 10.0.401 (or an allowed servicing patch), Node 22.22.3 and Python 3 for verification helpers.

```sh
npm ci --ignore-scripts
npm run build:css
dotnet tool restore
dotnet restore --locked-mode
dotnet run --project src/AllenBuilding.Web -- --migrate
dotnet run --project src/AllenBuilding.Web -- --urls http://localhost:5081
```

Open http://localhost:5081. Migrations create an empty database; there are no default accounts. Create the first manager from a separate interactive terminal:

```sh
dotnet run --project src/AllenBuilding.Web -- --bootstrap-manager
```

Passwords accept 12–128 characters in activation/reset forms; numbers, capitals and symbols are optional. First sign-in uses email/password and opens the portal. Set up account security separately when ready; manager tools still require MFA. Initial setup verifies the authenticator once and displays recovery codes directly, without another sign-in. Bootstrap refuses an existing account database. Keep `.data`, credentials and real tenant records out of Git.

Managers invite tenants from the portal. Development mail stays in the private local inbox; process pending invitations with:

```sh
dotnet run --project src/AllenBuilding.Web -- --dispatch-mail-once
```

Read the invitation link from `src/AllenBuilding.Web/.data/mail`. Use separate browser profiles for manager and tenant testing.

## Demonstration data

For a recorded walkthrough, a console tool writes a separate local database that looks like the Allen Building after two months of use: seven tenants, two managers with MFA enrolled, four contractors, about thirty maintenance requests at every stage, published help, notification history, and a rent account for each tenant with rent terms, two months of rent, three one-time charges and credits a manager recorded for rent paid by check. Five tenants are paid up and two are behind. Every person and business is fictional and every address ends in `@example.test`. It is never a web route or a production command, it holds no payment, autopay or payment-provider data, and it sends no email.

```sh
dotnet run --project tools/AllenBuilding.DemoSeed -- artifacts/demo/video
```

The tool only writes to a new `artifacts/demo/<name>` directory inside this repository and refuses to overwrite an existing database. It prints a count for each kind of record and exits non-zero if any count differs from the demonstration specification. The directory holds the SQLite database, its Data Protection keys and an owner-only `accounts.json` with the sign-in emails, the shared demo password and each manager's authenticator key.

Start the web app against that directory with rent planning on. Payment collection, autopay, refunds and the financial-exception pages stay off; nothing in this command or the development settings can turn them on:

```sh
DEMO="$PWD/artifacts/demo/video"
ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Office="Data Source=$DEMO/office.db" \
  Protection__KeyDirectory="$DEMO/keys" Billing__PlanningEnabled=true Notifications__Enabled=true \
  dotnet run --project src/AllenBuilding.Web --no-launch-profile -- --urls http://localhost:5081
```

Both paths must be absolute. Records are created through the same application actions the pages use, with the clock set to each moment, so requests carry real history, versions and receipts. All dates count back from the moment the tool runs: seed a fresh directory on the day of recording, because ages keep growing and the open invitation expires five days after seeding. `Notifications__Enabled=true` turns on the background sender against the local inbox, so a status change made during the walkthrough goes from waiting to send to delivered within about five seconds. Nothing leaves the machine: each message is written as a file under `src/AllenBuilding.Web/.data/mail`. The sender also delivers the two seeded waiting emails a few seconds after startup; leave the setting out to keep them waiting, and deliver on demand by running the same command with `-- --dispatch-mail-once` in place of `-- --urls ...`. The seeded failed email stays failed either way until a manager retries it. Links in emails use `http://localhost:5081/`; set `Notifications__PublicOrigin` if the site runs elsewhere.

## Useful review paths

- Public page, published help, and System/Light/Dark at a narrow viewport.
- Tenant request creation, search, history and comments; another tenant must not see the request.
- Manager triage, assignment, start, completion and separate verification/closure.
- Assistant help, draft/edit/cancel/review and explicit confirmation; sign-in alone never submits a guest draft.

The optional development assistant reads `OPENAI_API_KEY` from ignored repository-root `.env.local`, restricted with `chmod 600 .env.local`. Never paste a key into chat or commit it. Restart the server after replacement. The ordinary portal works without it. Production does not load this developer file and defaults to disabled assistance.

Local assistant limits use the signed-in person, or the visitor's IP when anonymous: five turns/minute, twenty admissions/hour and two concurrent review/help-page requests. The server-wide ceiling remains one hundred admissions/day. These are in-memory development limits; production still requires durable cost controls. Displayed timestamps use Eastern time with EST/EDT labels; stored instants remain UTC and calendar due dates are unchanged.

## Three engineering stories

**Authorization and concurrency:** controllers call shared application use cases. SQL scopes records to their original tenant membership before loading them. Manager transitions require MFA and reviewed versions; ordinary tenant comments append safely despite intervening manager activity.

**Reliable notifications:** accepted work, history, replay receipts and appropriate outbox messages commit together. Lease fencing, retry/suppression and provider-acceptance ambiguity have executable checks. Comments/internal notes generate no status email; status updates go to the requester.

**Human-confirmed assistance:** typed model output can prepare a draft but cannot execute or approve a change. Server-owned reviews bind original authority and content; a separate browser confirmation uses the same maintenance transaction as the ordinary form.

## Verification

```sh
dotnet build --configuration Release --no-restore --disable-build-servers -m:1
dotnet test --configuration Release --no-build
./scripts/verify-mysql.sh
npx playwright install chromium webkit
npm run test:browser
dotnet format --verify-no-changes --no-restore
python3 -m unittest discover -s tests/scripts -v
```

The MySQL script requires Docker and creates a disposable loopback database. Its shared contract is skipped by ordinary tests and runs separately. Browser fixtures use isolated synthetic accounts/data. Coverage: `./scripts/coverage.sh`; run it alone with the preview stopped because it instruments shared assemblies. Reports and screenshots remain under ignored `artifacts/`. Percentages are improvement goals; behavioral checks gate releases.

Current local verification (branch `feature/demo-billing`, 2026-11-11): 134 domain and 608 ordinary integration checks, the separate actual MySQL contract, 27 browser journeys and nine Python helper checks pass, and Release has zero warnings/errors. The SQLite persistence contract reports each of its 38 contract checks as a separately named test on a fresh database; MySQL runs the same list as one sequential test. No schema changed: scratch migrations for both providers were empty after the model configuration split. Coverage (`./scripts/coverage.sh`, SQLite run) is 92.4% of lines and 83.7% of branches overall; Web is 90.6% and 82.4%. MySQL-only lock statements count as uncovered because coverage is not collected during the MySQL run. Browser emulation does not establish real-device acceptance.

Hosted verification: [a successful Verify run](https://github.com/jeremyvaught/allenxenia.office/actions/runs/43109856558) tested `c9cf14c`, the merge of the signed-in UI changes to `main`, including actual MySQL, browser journeys, coverage, Linux packaging and independent restore. The demonstration seed, the walkthrough fixes and the rent-planning pages on `feature/demo-billing` have not had a hosted run. This establishes hosted verification, not operation on the actual Forge server.

## Scope and launch limits

Rent planning is what the billing pages deliver: a manager opens a rent account, sets rent terms, charges rent for a month, adds one-time charges and records a credit against a charge (for example rent received by check); a tenant sees their terms, charges and what they owe; the rent balance report totals what is owed and overdue. Each change is reviewed on a separate page before it is saved. These pages appear only when `Billing:PlanningEnabled` is set, which production does not do.

Payment collection is built and covered by tests but switched off server-side. It is incomplete and is not presented as delivered. Taking payments, autopay and refunds can only be switched on inside the isolated test environment, and the payment-provider webhook receiver is a disabled stub; with them off their pages answer 404 and nothing links to them. The financial-exception queue is hidden the same way unless `Billing:FinancialReviewEnabled` is set. A recorded credit is a manager's decision that reduces what is owed; it does not take or verify a payment. Ending a tenancy from the billing page works, but its follow-up cleanup list belongs to payment collection and is hidden with it.

Monthly rent, one-time charges and verified receipts are the intended payment scope. Refunds are handled manually by check; automated refunds, disputes and elaborate financial exceptions are outside scope. Dormant financial code is incomplete and must not be enabled as delivered functionality.

The canonical operations runbook records deployment of `e04ce78` to office.allenxenia.com and restoration of its downloaded MySQL/key snapshot. These new delivery/content fixes are not deployed yet. Real SMTP delivery, scheduled off-server backups and alerts, owner MFA enrollment, and a tenant pilot remain unfinished. Assistant persistence/stream replay, durable production cost controls and broad model evaluation are later disabled stages.

Forge templates are in `ops/forge`; release verification helpers are in `scripts`. Production requires explicit trusted proxy addresses, separate MySQL runtime/migration credentials, persistent wrapped Data Protection keys and approved private configuration. `AllenXeniaOffice.Build` is the fixed encryption namespace: changing a display name must never change it.

Build a committed Linux x64 release with `python3 scripts/package-release.py build`, then pass its output directory to `python3 scripts/verify-release.py <release-directory>`. The self-contained package supplies .NET; the accompanying `.tar.gz` preserves executable permissions. Confirm the actual host architecture and OS libraries before deployment. Runtime security updates require rebuilding and deploying a serviced package. `build --profile portable` remains available for hosts with an explicitly managed .NET runtime.

The canonical deployment, MySQL/key backup and independent restore procedure is the owner's vault document `dotnet/Development/allen-xenia-office-operations.md`. Configure and demonstrate its off-host schedule before admitting a real tenant; a local disposable-container restore is separate evidence. Prefer Forge's database backups with a separate protected-key backup over adding another server package.

Operator MFA recovery uses an interactive, disabled-by-default deployment command (`--recover-manager-mfa`) after independent owner verification. It issues a 15-minute single-use code delivered outside the reset-email inbox, then requires authenticator enrollment, a fresh MFA sign-in and recovery codes. Email/password alone cannot complete recovery.

Detailed design, the project brief and historical evidence belong in the canonical Obsidian project under `dotnet/Development`. Earlier README checkpoints remain available through Git history; unfinished capabilities are not production-readiness claims.
