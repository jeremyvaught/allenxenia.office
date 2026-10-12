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

The foundation has domain rules, Identity persistence and separate SQLite/MySQL migrations; tenant sign-in/workflows and external providers are still being built. No production readiness is claimed. Local migrations create an empty database, not default tenant credentials. Never use real tenants or payments in test fixtures.

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

The shared MVC layout uses compiled Tailwind v4 and semantic light/dark tokens. System is the initial preference; explicit Light/Dark is retained on the browser and System removes the override. The public page accurately identifies tenant access as being prepared until account workflows ship.

```sh
npx playwright install chromium
npm run test:browser
```

Browser tests cover System/OS changes, explicit preference across reloads, CSS delivery and 320px layout on desktop/mobile emulation. They use their own server on loopback port 5088. Emulation is not evidence of a real iPhone/Android acceptance run. `npm run dev:css` watches styles during development; rebuild CSS before .NET build/publish so static-asset manifests include it.

The build pins `@parcel/watcher` 2.6.0 through an npm override to remove the unpatched `braces` dependency found during the initial audit. Recheck CSS compilation and watch behavior when changing that pin.
