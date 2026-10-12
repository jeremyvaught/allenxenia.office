# Allen Xenia Office build

This is a new implementation in `/Users/jeremyvaught/sites/allenxenia.office-build`. Do not copy or modify the old application as the implementation strategy. Use it as behavior/evidence reference.

Follow the canonical PRD and spec in `/Users/jeremyvaught/markdown/dotnet/Development/`. Owner instructions override prior incremental-migration assumptions: build from the ground up; SQLite for local development and MySQL for production. Keep business outcomes consistent across both providers; use separate provider migrations and real MySQL integration tests for constraints, transactions and locks.

Use MVC/Razor, built-in DI and feature-oriented Domain/Application/Infrastructure/Web layers. Use Tailwind v4 with System/Light/Dark (System default), responsive public and authenticated flows. Authorization and consequential-action confirmation are server responsibilities. Provider calls occur outside database transactions.

Commit at logical milestones after Release build, relevant tests and formatting checks. Never stage credentials, real tenant data, `.data`, generated test artifacts, database files or local configuration. Do not push or deploy solely because a commit was requested. Report unfinished capabilities and unverified provider/host behavior accurately.

Authored knowledge stays in the canonical vault; executable/repository setup remains here. Avoid extra frameworks, generic repositories and interfaces for every class. Do not disable failing critical tests to claim readiness.
