# Roadmap

Data Scope is built in small milestones, each with code, tests and documentation. Decisions and their reasons are in `DECISIONS.md` (the D numbers below).

## What is built

| Area | What you get |
|---|---|
| Inventory | Enrol Windows file servers, list their shares, walk folders safely (no link following, bounded depth and counts), quick and full scans, schedules and scan windows, exclusions. |
| Classification | Possible matches with confidence and redacted evidence for US SSNs and ITINs, payment cards, bank numbers, IBANs, credentials and private keys, health information, dates of birth, driver's licence and passport numbers, names with addresses; custom rules. Files are read by their contents: Office (new and 97-2003), PDF, email, Outlook .msg/.pst/.ost, zip, 7-Zip and RAR, and text in images and scanned pages by local OCR. |
| Active Directory and access | Directory sync over LDAP, nested groups, share and NTFS permissions, who can open what and why, with certainty and coverage. |
| Exposure and risk | Exposure map, risk list with triage, compliance standards and evidence report, stale and duplicate data. |
| Owners and access reviews | Data owner registry, frozen review campaigns, email through your SMTP relay, reminders and escalation, decisions kept as signed evidence. |
| File activity | Windows Security log collection, alerts (mass deletes, permission changes, refused attempts), unusual behaviour per person, retention and reports. |
| Operations | Progress over time, System health page, Find a person for subject access requests, signed install package, one-file setup wizard with PostgreSQL inside, backups and restore, SBOM. |

Data Scope never changes files, permissions or Active Directory. Reviews and risks tell people what to change; they make the change themselves.

## Next

- **Search scan for a person:** read chosen shares again and list every file whose text mentions a person, without storing the text.
- **Upgrade path tests:** this edition starts from a single database migration, so the tests that upgrade from an older schema come back with the second migration.
- **Windows lab verification:** steps marked UNVERIFIED in `WINDOWS-LAB.md` need a run on a real domain.
- **MSI package** for organisations that deploy with Intune or SCCM (D89).
- **More detectors:** non-US national identifiers and state ID cards.

## Not planned

Microsoft 365, cloud services, telemetry, remediation scripts, automatic changes to files, permissions or Active Directory, and automatic deletion. Ideas and pull requests are welcome; see `CONTRIBUTING.md`.
