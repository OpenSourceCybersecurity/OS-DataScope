# Product requirements | Data Scope

## Vision and users
A free, open source, straightforward, fully on-premises data governance system for SMBs with Windows file servers and Active Directory. Primary users: IT manager, MSP, security lead. Other users: data owners/department managers, auditors. Positioning: identify sensitive data, map who can access it, certify access, reduce unnecessary exposure, and retain defensible evidence.

## Supported environment V1
- Windows Server-hosted NTFS/SMB shares, on-prem AD DS, internal SMTP relay, modern Chromium/Edge browsers on LAN.
- Initial pilot: 1 management server, 1-3 file servers, one AD domain; scale targets are provisional and must be benchmarked.
- Unsupported initially: SharePoint, Exchange, OneDrive, Entra ID, NAS/Samba, Linux, macOS, cloud-hosted control plane, automatic AD edits, automated file deletion, OCR of images, full DLP.

## User roles
- Platform Admin: system setup, sources, scanning policy, RBAC.
- Security Analyst: findings, searches, risk triage, exports.
- Auditor: read-only evidence/reports and review history.
- Data Owner: only assigned shares/reviews, may approve or request changes, cannot change ACLs.
- Read-only Viewer: scoped dashboard access.
Default deny; every API endpoint must enforce server-side authorization and resource scope.

## Core module A: Inventory/discovery (V1)
- Discover shares on explicitly enrolled Windows servers and enumerate permitted roots.
- Index file metadata: stable source/server/share identity, normalized path, extension, size, created/modified times, scan status, last scan, access errors.
- Scheduled full and incremental scans, pause/resume, cancellation, retries, bounded parallelism, bandwidth/CPU/IO limits, exclusion rules, reparse-point handling, progress/coverage metrics.
- Never modify file contents, timestamps intentionally, ACLs, or ownership. Record access-denied and skipped files; do not silently report them as clean.
- File formats V1: TXT, CSV, JSON, XML, DOCX, XLSX, text-based PDF. Other formats reported unsupported. Password-protected/encrypted files reported unscannable.

## Core module B: Detection/classification (V1)
- Detect possible US SSNs (pattern + invalid-range checks + context), payment cards (Luhn + context), routing/account combinations, credentials/private-key material, contextual health information; custom administrator regex rules with safe timeouts.
- Store minimum necessary redacted evidence and policy/rule identifiers; do not persist entire file contents or unmasked sensitive strings by default.
- Classification: Unknown, Public, Internal, Confidential, Restricted; configurable rule-to-classification mappings, confidence, reviewer override, reason, policy version, audit history.
- Test for precision/recall using synthetic positive and negative fixtures. Explicitly show confidence and coverage, not guarantees of finding all sensitive data.

## Core module C: SMB/NTFS/AD permissions (V1)
- Snapshot share DACLs, NTFS DACLs, inheritance, allow/deny, SIDs, rights and path scope; preserve raw ACE details.
- Resolve domain principals, direct/nested group memberships, disabled users, unresolved SIDs, cycles and cross-domain/foreign-security-principal cases where possible.
- Explain why a user is in a permission result (membership chain + relevant ACEs). Consider both share and NTFS constraints and deny/inheritance semantics. Clearly distinguish 'likely access', 'confirmed by Windows access check', and 'unknown/partial' when unable to fully compute effective access.
- Highlight broad groups, orphaned identities, unexpected privileged groups, excessive rights, and ACL drift. Do not treat group membership alone as proof of file access.
- V1 should focus on shares and selected sensitive folders; don't promise per-file effective access at unlimited scale.

## Core module D: Data owner registry (V1)
- Assign one primary owner and optional backup owner to a share/folder; map department, purpose, sensitivity, review cadence, retention category, and notes.
- Do not automatically guess authoritative ownership from AD or filesystem owner SID. Provide suggestions only and require confirmation.
- Track reassignment and orphaned owners.

## Core module E: Access certifications (V1 signature)
- Create quarterly/semiannual/annual campaigns based on frozen ACL + AD membership snapshot with immutable campaign revision.
- Send authenticated review links via configured on-prem SMTP; never approve via email link alone.
- Owners review each identity or group, see access path, role/department if available, and uncertainty indicators. Choices: Keep, Request removal, Investigate, Exception with justification; support comments and bulk actions with confirmation.
- Reminders, due dates, backup-owner escalation, overdue states, delegation with audit trail.
- Revoke request creates an IT task only; no direct AD change. Explain group-removal blast radius and alternative access paths.
- Export campaign evidence: snapshot time, scope, reviewed principals, access path, reviewer, decisions, justification, status, verification.

## Core module F: Reporting and risk (V1)
- Dashboard: scan coverage, sensitive findings, high-risk access, unowned shares, overdue reviews, data volume trends.
- Reports: Sensitive Data Inventory; Share/Owner Inventory; AD Group Access Matrix; Risky Permissions; Access Certification Evidence; Scan Coverage/Failures.
- PDF/CSV export with filters, generation time, policy version, data freshness and known limitations.
- Explainable severity combining sensitivity, exposure, confidence, and recency. Avoid opaque AI scores.

## Module G: Lifecycle cleanup (V1.5)
- Stale files using configurable last-modified windows; optional hash-based duplicates with safe resource bounds; abandoned shares, orphaned owners, retention review queue.
- Last-access timestamps can be unreliable/disabled; do not treat as authoritative. No automatic deletion. Respect legal holds/retention policy and record approvals.

## Module H: Windows file activity auditing (V1.5)
- Optional lightweight Windows service on each enrolled file server; ingest selected Windows Security audit events after admin configures appropriate audit policy/SACLs.
- Preserve event record ID, channel, provider, host, event time, ingestion time, principal, path/handle/object where available, operation, result/uncertainty, and raw source reference.
- Candidate events: 4663, 4656, 4660, 4670, 5140, 5145; use event-specific semantics and correlations. 5145 is a share access check, not proof of content read; 4660 may lack path.
- At-least-once durable buffering, bookmark/restart recovery, idempotent ingest, gap detection, retention controls, rate limiting, collection health, alerting for mass deletion/ACL changes. Document audit-volume overhead.

## Out-of-scope V1
Microsoft 365 connectors, automatic AD changes, auto-delete, legal-hold enforcement, content-based OCR, autonomous AI classification requiring cloud, ransomware prevention, replacing SIEM/EDR, guaranteed compliance certification.

## UX requirements
Simple onboarding wizard: add server, verify permissions, select roots, set scanning limits, scan, review results, assign owner, launch access review. Plain-English risk explanations; distinguish pending/unknown from safe; search by user/share/path/type; accessible and responsive web UI.

## Product acceptance
An SMB admin can install offline, inventory shares, find seeded PII, explain permission exposure, assign a manager, email an authenticated quarterly review, record owner decisions and export audit evidence. No source files or AD permissions modified.
