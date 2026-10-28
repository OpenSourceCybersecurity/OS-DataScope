# Acceptance and Windows Lab Test Plan

## Test lab
Isolated domain controller + Windows file server + management VM, test-only domain and users. Create groups `HR_Read`, `HR_Write`, `Finance_Read`, `AllStaff`, nested `Finance_Reviewers`, a disabled user, and a group nesting cycle where possible. Configure shares `HR`, `Finance`, `Public`, `Operations` with inheritance, explicit allow/deny, and mixed share/NTFS ACLs. Generate synthetic DOCX/XLSX/PDF/CSV/TXT fixtures with known fake SSNs, fake payment card test numbers, benign nine-digit strings, mock credentials, encrypted and malformed documents. No real personal information.

## V1 end-to-end acceptance
A1. Offline installer starts service/web UI and works without external DNS/internet.
A2. Non-admin scan identity inventories only permitted shares; denied roots appear as incomplete with actionable error.
A3. Seeded PII files are detected; known benign fixtures have measurable false positives; confidence and rule IDs shown.
A4. Unsupported/encrypted/corrupt files are counted as unscanned, not clean.
A5. Scanner does not modify source file contents, ACLs, ownership, or timestamps intentionally; verify before/after hashes and ACLs.
A6. Share and NTFS ACL snapshots preserve explicit/inherited allow/deny and correct SID identities.
A7. Nested group resolution explains membership path, flags cycles/unresolved SID, and never overstates partial effective access.
A8. A user with share-level restriction is not presented as having unrestricted effective rights solely because of NTFS rights.
A9. Owner assignment and role scoping prevent one manager viewing another department's review.
A10. Campaign snapshot remains unchanged after later AD membership modifications.
A11. Email sends only an authenticated review link; expired/replayed unauthorized access fails.
A12. Keep/Remove/Investigate/Exception decisions preserve reviewer, timestamp, justification and immutable evidence.
A13. A Remove decision is recorded with its reason, does not modify AD, and shows alternative group paths/blast radius.
A14. A rescan records an externally performed permission change as drift.
A15. Reports state time, scope, coverage, failures, policy version, and incomplete access resolution.
A16. Restart during scan and DB/AD/SMTP outages do not corrupt data or falsely mark work complete.
A17. Restore database from backup and complete documented rollback test.
A18. Unprivileged API calls and ID tampering cannot read or modify another role's data.
A19. CSV/PDF exports safely escape malicious values and redact restricted evidence.
A20. Scan load can be bounded, paused and resumed; benchmark results documented.

## V1.5 auditing acceptance
B1. Configure Windows audit policy and SACLs explicitly; verify expected events on supported Windows Server versions.
B2. Controlled create/write/delete/permission-change cases yield event records with source evidence.
B3. Share access checks are not mislabeled as confirmed file reads.
B4. 4660 deletion correlation handles absent path and marks ambiguity when needed.
B5. Collector restart/replay does not duplicate normalized events; log rollover/gaps are surfaced.
B6. Mass deletion alert triggers for synthetic threshold test, with noise/false-positive tuning.

## CI test groups
- Unit: classification, paths, RBAC policy, permission interpretation, review state machines.
- Integration: PostgreSQL migrations, API endpoints, job queue, SMTP mock, directory abstractions.
- Windows lab: actual NTFS/SMB/AD/Windows Event Log behavior; do not claim cross-platform mocks prove these.
- Security: authz, malicious documents, resource exhaustion, secrets/logging, export injection.
- Packaging: clean install, offline startup, upgrade, uninstall, restore.

## Acceptance reporting
For every requirement report PASS/FAIL/UNVERIFIED, evidence/log link, environment, and known limitation. A milestone cannot be called production-ready based solely on unit tests.
