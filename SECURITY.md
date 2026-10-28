# Security policy

Data Scope runs inside your network and reads their file servers and Active Directory. This page says how to report a security problem, how fixes are released, and how to check that a package is genuine.

## Reporting a vulnerability

Report security problems privately, never in a public issue. Use GitHub's private vulnerability reporting: on the repository's **Security** tab, choose **Report a vulnerability**.

Please include the Data Scope version (the package name, or the version shown in a support bundle), what you did, what happened, and whether it needs an authenticated user. Do not send real files, scan results, file names, account names or support bundles that hold real data; synthetic examples are enough.

Data Scope is maintained by volunteers. We aim to acknowledge a report within 5 days and keep the reporter informed until it is fixed. We credit reporters who want it once a fix is released.

## Supported versions

Only the latest release is supported. Security fixes go into a new release; there are no back-ported patches. Upgrading is in place and takes a backup first (`docs/INSTALL-DATASCOPE.md`, "Upgrading").

## Patch policy

Severity uses CVSS 3.1 together with Data Scope's deployment (on-premises, behind Windows sign-in).

| Severity | Aim to release a fix within | Users told |
|---|---|---|
| Critical (for example, unauthenticated access to data or code execution) | 7 days | A security advisory, with a workaround if there is one |
| High | 30 days | A security advisory, with the release |
| Medium | 90 days, or the next release | Release notes |
| Low | Next release | Release notes |

Third-party components count too. Every build fails on a NuGet package with a known vulnerability (`dotnet list package --vulnerable`) or a high-severity npm advisory in the web UI's shipped packages (`npm audit --omit=dev`), and each package's SBOM (`sbom.cdx.json`, CycloneDX 1.5) lets you check their own scanners against exactly what shipped.

## Verifying a package

Each release is a zip with three things to check, all offline:

1. **Hash.** `DataScope-<version>.zip.sha256` holds the zip's SHA-256: `Get-FileHash .\DataScope-<version>.zip -Algorithm SHA256` must match it, and must match the hash published with the release notes.
2. **Signature.** Data Scope's own programs and scripts are Authenticode-signed (`Sign-DataScopePackage.ps1`, D91): `Get-AuthenticodeSignature .\Install-DataScope.ps1` shows Valid and the publisher's name. Third-party files keep their own publishers' signatures.
3. **Installer check.** `.\Install-DataScope.ps1 -PreflightOnly -RequireSignature` checks every file against `SHA256SUMS` and every Data Scope program and script against the installer's own signature, and changes nothing.

Release signing keys are kept off the build servers and out of source control; CI only ever signs with a throwaway test certificate.

## What Data Scope does with data

- It runs entirely on your servers. It needs no internet access, sends no telemetry and calls no cloud service or AI service.
- It only reads: it never changes, moves or deletes files, permissions or Active Directory objects. Removals decided in reviews are recorded as evidence; people make the change.
- Matched text is never stored: findings keep a masked sample (`***-**-1234`) and the rule that matched.
- Passwords in its settings are encrypted with Windows DPAPI; the services' database login cannot change the schema or rewrite the audit log, or review history (D78, D79).
- Backups are encrypted with a per-server key (D84, D85).
- Support bundles contain no file names, paths, people or matched text, and are only ever written to a local file (D93).

The threat model is in `docs/THREAT-MODEL.md`, and what the results can and cannot show is in `docs/DETECTION-LIMITATIONS.md`.

## Security incidents

If you suspect Data Scope was involved in an incident: stop both services (`Stop-Service DataScopeApi, DataScopeScanner`), keep `C:\ProgramData\DataScope` and the database as they are, export the audit log (`/api/v1/audit-log`, and `/api/v1/audit-log/verify` to check it has not been changed), and report it as above. A support bundle (`DataScope.Setup.exe support-bundle`) holds no file names, people or matched text.
