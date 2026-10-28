# Security, Privacy, and Threat Model

## Trust boundaries
Untrusted document content, filenames, UNC paths, AD attributes, Windows audit event strings, SMTP content, user-supplied classification regex, browser input, API requests, local DB, privileged Windows services, and installer/update mechanism. Treat all as potentially malicious input.

## Minimum controls
- Separate scanning read-only credentials from the web admin identity. No Domain Admin required or permitted as default.
- Encrypt all browser/API/collector traffic using HTTPS/TLS; certificate enrollment/replacement documentation; avoid insecure default certificates in production.
- Integrated AD authentication with RBAC; enforce authorization on every endpoint and report export; anti-CSRF for cookie auth; secure session handling.
- Protect database credentials, SMTP credentials, API tokens and service secrets with OS-supported protected storage; never store secrets in Git, logs, browser local storage or config defaults.
- No outbound telemetry or external services required. Do not ship hidden network calls.
- Redact PII in logs and UI by default. Findings evidence must be limited, encrypted, and access-controlled; full document content is never stored by default.
- Guard against archive bombs, hostile PDFs/Office docs, XML external entities, path traversal, reparse loops, UNC injection, unbounded regex, excessive memory use, and malicious CSV exports.
- Document Windows event log audit policies and SACL prerequisites, overhead and gaps. Track dropped events, rollover, clock drift and incomplete correlations.
- Protect review evidence from tampering with append-only semantics and integrity verification. State that local machine/database administrators remain within trust boundary.
- Secure software supply chain: pinned dependencies, lockfiles, SCA, secret scanning, SBOM, provenance, code signing, reproducible builds where feasible.
- Signed MSI releases; no automatic execution of code from update feeds. Separate release credentials from development agent.
- Retention configuration for findings, audit events, backups, review evidence, and application logs. Secure deletion process and legal-hold exceptions where supported.
- Admin action logging, role changes, policy changes, login attempts, exports, data owner overrides, review submissions.

## Mandatory negative tests
- Unauthorized manager tries to view another department's review or files.
- User tampers with share/folder ID in API URL.
- Malicious filename contains HTML/script or path traversal.
- CSV cells begin with `=`, `+`, `-`, or `@`.
- Recursive junctions and inaccessible directories.
- Oversized malformed PDF/DOCX/XLSX and regex catastrophic backtracking.
- Interrupted scan/DB outage/SMTP outage/AD outage; correct retry and status.
- Group nesting cycles, deleted users, unresolved SIDs, deny ACEs, cross-domain partial results.
- Time skew, duplicate audit events, log rollover, collector restart.
- Backup restore and failed schema migration.

## Release gate
No external pilot until architecture and threat model reviewed by a qualified engineer, authz penetration tests pass, synthetic Windows integration tests pass, installers/upgrades and backup restores tested, secrets scanning and dependency review complete, and documentation clearly describes detection/coverage limitations.
