# Allen Xenia Office build

This repository is the new ground-up implementation. Do not copy or modify the old application as the implementation strategy. Use it as behavior/evidence reference.

Follow the canonical PRD and spec in the owner’s Obsidian vault under `dotnet/Development/`. Owner instructions override prior incremental-migration assumptions: build from the ground up; SQLite for local development and MySQL for production. Keep business outcomes consistent across both providers; use separate provider migrations and real MySQL integration tests for constraints, transactions and locks.

Use MVC/Razor, built-in DI and feature-oriented Domain/Application/Infrastructure/Web layers. Use Tailwind v4 with System/Light/Dark (System default), responsive public and authenticated flows. Authorization and consequential-action confirmation are server responsibilities. Provider calls occur outside database transactions.

Commit at logical milestones after Release build, relevant tests and formatting checks. Never stage credentials, real tenant data, `.data`, generated test artifacts, database files or local configuration. Do not push or deploy solely because a commit was requested. Report unfinished capabilities and unverified provider/host behavior accurately.

Authored knowledge stays in the canonical vault; executable/repository setup remains here. Avoid extra frameworks, generic repositories and interfaces for every class. Do not disable failing critical tests to claim readiness.

Skill exceptions (from the 2026-11 review), so later runs do not undo them: do not add `ConfigureAwait(false)` (ASP.NET Core app); no `Span<T>`/`Memory<T>`/`ArrayPool` without a measured hot path; `OfficeDbContext` as an abstract base for `SqliteOfficeDbContext`/`MySqlOfficeDbContext` is intended; use `readonly record struct` value objects and Result types in new code where natural, but do not convert existing entities or result codes wholesale. The test skills are diagnostic only; their companion skills are not installed, so use their built-in rules.
