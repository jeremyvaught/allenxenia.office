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

First sign-in uses email/password and opens the portal. Set up account security separately when ready; manager tools still require MFA. Initial setup verifies the authenticator once and displays recovery codes directly, without another sign-in. Bootstrap refuses an existing account database. Keep `.data`, credentials and real tenant records out of Git.

Managers invite tenants from the portal. Development mail stays in the private local inbox; process pending invitations with:

```sh
dotnet run --project src/AllenBuilding.Web -- --dispatch-mail-once
```

Read the invitation link from `src/AllenBuilding.Web/.data/mail`. Use separate browser profiles for manager and tenant testing.

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

First-sign-in revision: 134 domain and 491 ordinary integration checks and twenty-four browser journeys pass, including native 320px first login and optional setup. Release has zero warnings/errors. Nine Python helper checks passed at the preceding checkpoint. The shared actual MySQL contract was verified in the earlier remediation; no SQL/schema changes occurred in this follow-up. The preceding revision `a5a21b1` supplies a verified 393-file self-contained Linux x64 package; styled missing-page/article responses, isolated Production/MySQL restore and wrapped-key continuity pass on an image without .NET installed. Browser emulation does not establish real-device acceptance. Coverage remains from the earlier assistant checkpoint.

## Scope and launch limits

Payment collection is disabled server-side. Monthly rent, one-time charges and verified receipts are the intended payment scope. Refunds are handled manually by check; automated refunds, disputes and elaborate financial exceptions are outside scope. Dormant financial code is incomplete and must not be enabled as delivered functionality.

Real SMTP delivery, the actual Forge runtime/database configuration, production backups/key recovery, off-host restore, hosted CI and a tenant pilot still need evidence. Assistant persistence/stream replay, durable production cost controls and broad model evaluation are later disabled stages.

Forge templates are in `ops/forge`; release verification helpers are in `scripts`. Production requires explicit trusted proxy addresses, separate MySQL runtime/migration credentials, persistent wrapped Data Protection keys and approved private configuration. `AllenXeniaOffice.Build` is the fixed encryption namespace: changing a display name must never change it.

Build a committed Linux x64 release with `python3 scripts/package-release.py build`, then pass its output directory to `python3 scripts/verify-release.py <release-directory>`. The self-contained package supplies .NET; the accompanying `.tar.gz` preserves executable permissions. Confirm the actual host architecture and OS libraries before deployment. Runtime security updates require rebuilding and deploying a serviced package. `build --profile portable` remains available for hosts with an explicitly managed .NET runtime.

The canonical deployment, MySQL/key backup and independent restore procedure is the owner's vault document `dotnet/Development/allen-xenia-office-operations.md`. Configure and demonstrate its off-host schedule before admitting a real tenant; a local disposable-container restore is separate evidence. Prefer Forge's database backups with a separate protected-key backup over adding another server package.

Operator MFA recovery uses an interactive, disabled-by-default deployment command (`--recover-manager-mfa`) after independent owner verification. It issues a 15-minute single-use code delivered outside the reset-email inbox, then requires authenticator enrollment, a fresh MFA sign-in and recovery codes. Email/password alone cannot complete recovery.

Detailed design, the project brief and historical evidence belong in the canonical Obsidian project under `dotnet/Development`. Earlier README checkpoints remain available through Git history; unfinished capabilities are not production-readiness claims.
