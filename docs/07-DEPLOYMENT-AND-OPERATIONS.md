# Deployment, Administration, and Support Requirements

## Installer/onboarding
- Preflight supported OS, CPU/RAM/disk, DB availability, Windows service permissions, AD connectivity, SMTP, certificate.
- Securely configure management service identity and optional scanning identity; document minimum NTFS/SMB and AD read rights.
- Configure HTTPS and role bootstrap; require changing/rotating any setup credentials.
- Enroll Windows file servers and select explicitly approved scan roots. Dry-run connectivity/permissions test.
- Offer offline install, upgrade and uninstall with no silent data deletion.

## Operations
- Daily health view: last scan, errors, stale directory snapshot, unreviewed findings, failed SMTP, disk usage, backup status.
- Scheduler: maintenance windows, throttling, blackout windows, retry backoff.
- Data retention: configurable by data class; evidence and reviews separate from transient scan logs.
- Backup: documented consistent DB backup and protected config/secrets; tested restore and migration rollback.
- Support bundle: redacted diagnostics only, explicit customer consent before export, no raw files or unmasked PII.
- Audit policy (V1.5): guided prerequisites and overhead estimator; event volume benchmarks.

## Release readiness
- Apache-2.0 licence and NOTICE, privacy/data handling notice, third-party license notices, support boundaries, documented detection limitations, minimum hardware, supported Windows versions, incident response/contact process, secure vulnerability reporting, release signing and security patch policy.
