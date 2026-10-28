# Data model

Every mutable business entity has immutable UUID, created_at, updated_at, row_version, tenant/install identifier (future-proof, no cloud multi-tenancy in V1), and relevant actor IDs. Use UTC. Preserve source IDs and snapshot lineage. No real PII in seed data.

## Inventory
- `managed_servers`: hostname, `kind` (`FileServer`; the top of the location tree), domain, enrollment state, last_seen, capability/coverage, scan schedule (`scan_frequency` Off/Hourly/Daily/Weekly, `scan_start_hour`, `scan_start_minute` 0 to 59, `scan_every_hours` 1/2/3/4/6/8/12 for Hourly only, `scan_day`, `scan_stop_hour`, with check constraints) and `last_scheduled_at`, the scheduled time last dealt with (D116, D121); full scans (`full_scan_frequency` Off/Weekly/Monthly, `full_scan_day`, required unless Off, and `last_full_scheduled_at`, D130).
- `shares`: server_id, share_name, UNC path, share ACL snapshot, scan status.
- `folders`: share_id, normalized/original path, parent_id, ACL inheritance, owner_registry_id. A partial trigram index (`pg_trgm`, GIN) on the path of current folders serves the search box (D128).
- `files`: share_id, folder_id, normalized/original path, size, extension, source timestamps, scan status, fingerprint metadata (optional), last_seen_scan_id. A partial index on (size, name_key) for current files finds likely duplicates (D119). A partial trigram index (`pg_trgm`, GIN) on the name of current files serves the search box (D128).
- `scan_jobs`: scope, policy_version, state, progress, errors, start/end, checkpoint; `scheduled` when the schedule started it and `keep_to_window` while it should pause at the server's stop hour (cleared by a hand pause, resume or cancel); `kind` Quick or Full (D130).
- `scan_errors`: job_id, path/reference, error class, message redacted, timestamp.

## Sensitive data
- `detection_policies`: name, version, type, expression/config, classification mapping, enabled.
- `classification_findings`: file_id, policy_version, detector, category, confidence, redacted_evidence, status, discovered_at.
- `classification_overrides`: file_id, class, reviewer, reason, timestamp, superseded_by.

## Directory and permissions
- `directory_principals`: SID, domain, sam_account_name, display_name, principal_type, enabled, resolution_status, observed_at.
- `group_membership_snapshots`: snapshot_id, parent_group_sid, member_sid, direct_or_nested, resolution_path, captured_at, completeness.
- `permission_snapshots`: share/folder resource, source, captured_at, coverage, raw ACL checksum.
- `permission_entries`: snapshot_id, SID, allow_deny, rights mask, inheritance/propagation flags, explicit/inherited, original ACE.
- `access_explanations`: snapshot_id, principal_sid, resource, derived rights, access path, confidence, limitations.

## Governance
- `data_owners`: AD SID, name, email, status, backup owner SID.
- `ownership_assignments`: resource, owner_id, department, purpose, retention category, review frequency, effective dates.
- `review_campaigns`: scope, owner, due date, status, frozen_permission_snapshot_id, frozen_group_snapshot_id, created_by.
- `review_items`: campaign_id, principal SID, resource, access summary, group chain, snapshot-derived explanation, state.
- `review_decisions`: item_id, reviewer SID, decision, justification, timestamp, immutable revision.
- `risk_decisions`: accept or snooze decisions on risks, with the risk id, a snapshot of its rule, title, place and severity, the reason, the end date, who decided and who reopened it. At most one active decision per risk; ended rows are kept.
- `exceptions`: scope, risk, approver, reason, expires_at, review status.

## Auditing V1.5
- `file_audit_events`: unique(source_server, channel, record_id, log_generation), event_id, raw_ref, event_time, ingest_time, principal_sid, resource, action, outcome, correlation_id, confidence.
- `collector_checkpoints`: server, channel, bookmark, last_success, last_gap, health.
- `alerts`: rule, evidence_refs, severity, state, assignee.

## Platform
- `app_audit_log`: actor, action, target, timestamp, before/after redacted metadata, integrity chain.
- `notification_outbox`: recipient, template, idempotency key, state, attempts, next_retry, last_error.
- `files.item_link` (D143): for a message, its Outlook on the web link; null for files on shares.
- `system_settings` (D123): operational settings saved on System settings: key (from a fixed list), value as JSON, updated at, by (SID and name). Applied over datascope.json; unknown keys or bad values are ignored. Changes are in `app_audit_log`.
- `scan_exclusions` (D124): folders and file types an administrator left out of scans: kind (Folder, FileType), effect (Skip: not scanned; ListOnly: listed, contents not read), share (required for a folder, optional for a file type), share-relative path or lower-case extension, reason, enabled, created and updated (at, by SID and name). A check constraint keeps folder rows with a path and no extension, and file type rows with an extension, no path and ListOnly. `shares` also records why a share was left out of scans, by whom and when. Excluded folders are `scan_gaps` of kind Excluded; files whose contents were left unread have content state Excluded.
- `compliance_standards` (D126): administrator choices per standard in the built-in catalog: standard id (key), enabled, categories (text array of detection categories, 1 to 16), minimum confidence (Medium or High), reason, updated at, by (SID and name). No row means off with the defaults. Which files fall under a standard is computed from current findings, never stored. Changes are in `app_audit_log`.
- `rbac_role_assignments`, `report_exports`, `schema_migrations`.

## Design rules
No plaintext SSNs/passwords in findings. Use paginated indexed queries, stable IDs, retention and migration plans. Snapshot entities must remain historically interpretable after AD/path changes. Define cascade/delete behavior explicitly to avoid destroying audit evidence.
