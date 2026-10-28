# Contributing to Data Scope

Thanks for helping. Data Scope is used to look at sensitive data, so the bar for safety is high, and these rules keep it that way.

## Ground rules

- **On-premises only.** No telemetry, no cloud service, no outside call at runtime.
- **Read-only.** Data Scope never changes files, permissions or Active Directory. People make changes; Data Scope records the decisions.
- **Honest results.** Detections are possible matches with confidence and evidence. Access answers show their certainty. Anything not read is reported as not read, never as clean.
- **Bounded scanning.** Every reader has limits on size, depth, decompression and time, and nothing found in a file, name or event is ever executed.
- **No real data.** Never commit or paste real personal data, customer files, account names, credentials or support bundles. Tests use synthetic fixtures.

## How to contribute

1. Open an issue first for anything bigger than a small fix, so we can agree on the scope.
2. Fork the repo and create a branch from `main`.
3. Build and test (see `docs/DEVELOPMENT.md`):
   ```bash
   dotnet restore --locked-mode
   dotnet build -c Release
   dotnet format --verify-no-changes
   dotnet test -c Release
   cd web && npm ci && npm run typecheck && npm test && npm run build
   ```
4. Add tests for every change, including negative and permission-denied cases. Don't weaken or skip tests to get a green build.
5. Database changes go in a new EF Core migration with notes on upgrade and rollback.
6. Record a design choice in `docs/DECISIONS.md` and any new risk in `docs/THREAT-MODEL.md`.
7. Open a pull request describing what changed, how you tested it, and anything that only a Windows lab can check (mark it UNVERIFIED and add steps to `docs/WINDOWS-LAB.md`).

## Licence

By contributing you agree that your contribution is licensed under the Apache License 2.0, the same as the project.
