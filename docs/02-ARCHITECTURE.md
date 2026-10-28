# Architecture

## Deployment model
One Windows Server joined to the domain (or an isolated lab VM) runs the Data Scope API and web UI, the scan worker and a local PostgreSQL 16. The worker reads file shares over SMB and Active Directory over LDAP, read-only, as a group managed service account (gMSA). File activity is read from each file server's Windows Security log. Nothing needs the internet at runtime: no telemetry, no cloud sign-in, no outside service. The UI and API are served over HTTPS inside the network.

```
 Browser (LAN, Windows sign-in)
        |
        | HTTPS (Negotiate / Kerberos)
        v
 +-----------------------+        +------------------+
 | DataScope.Api         |<------>| PostgreSQL 16    |
 | REST API + React UI   |        | (local)          |
 +-----------------------+        +------------------+
                                          ^
 +-----------------------+                |
 | DataScope.Worker      |----------------+
 | scans, AD sync,       |
 | activity, schedules   |---- reader processes (one file at a time, memory and time capped)
 +-----------------------+
     |  read-only SMB, LDAP, event log (gMSA)
     v
 Windows file servers and Active Directory
```

## Stack
- Backend: C# on .NET 10 LTS, ASP.NET Core minimal APIs, a .NET worker service.
- Frontend: React and TypeScript with Vite, a typed API client, no third-party scripts or fonts at runtime.
- Persistence: PostgreSQL 16 with EF Core migrations (Npgsql). The job queue is a table claimed with `FOR UPDATE SKIP LOCKED`; there is no message broker.
- Sign-in: Windows Negotiate (Kerberos, NTLM fallback). Roles come from AD group SIDs.
- Notifications: the organisation's own SMTP relay, through a database outbox.
- Install: a signed zip with a PowerShell installer, or the one-file setup wizard with PostgreSQL inside.

## Projects
| Project | What it does |
|---|---|
| `DataScope.Core` | Rules with no I/O: classification and detectors, access evaluation, risks, compliance standards, activity alert rules, settings. Most unit tests live here. |
| `DataScope.Persistence` | EF Core model, migrations, the job queue, the hash-chained audit log, stores. |
| `DataScope.Scanner` | Share listing, folder walking, Windows security descriptors, file readers (Office, PDF, email, Outlook data files, archives, images with OCR) and the reader process protocol. |
| `DataScope.ActiveDirectory` | LDAP directory reader (paged, ranged members) and a lab directory reader for development. |
| `DataScope.Api` | HTTP API, authorization policies, the web UI host, reports, reviews, risks, people search. |
| `DataScope.Worker` | Background work: scans, directory sync, activity collection and alerts, schedules, retention, backups. |
| `DataScope.Setup` | Command-line setup: database provisioning, configuration, backup and restore, checks, support bundle. |
| `DataScope.Installer`, `DataScope.Installer.Core` | The Windows setup wizard and its tested core. |
| `web/` | React UI. |
| `tests/` | Unit, database, directory and API tests, plus synthetic fixtures and the detection test pack. |

## Engineering rules
- Every long scan is a job with states, checkpoints, cancellation, a lease and a retry cap.
- Paths are normalised for matching and kept as found for display. Junctions, symlinks and mount points are never followed.
- File contents are read in bounded streams with caps on size, decompression, pages, characters and time. Readers run in separate processes with memory caps (a job object on Windows), so a file that crashes or hangs a parser costs only that file (D127). Files are read by their contents, not trusted by their names.
- Only metadata, redacted evidence snippets and hashes are stored, never whole files.
- Access answers come with an explanation and their certainty. Deny entries, share permissions and incomplete group resolution are shown, never collapsed into a false yes or no.
- Access reviews freeze permissions and group membership when a campaign starts. Decisions are kept as evidence; Data Scope does not make or track the changes.
- Time is stored in UTC; scan time, event time and ingestion time are kept apart.
- Every endpoint is deny-by-default with explicit authorization and paging.
- Administrative actions and exports are written to an append-only, hash-chained audit log in the same transaction as the change.

Decisions and their reasons are in `DECISIONS.md`; threats and controls in `THREAT-MODEL.md`.

## Nonfunctional targets
- 1 to 3 file servers and about a million files on one modest server (see `BENCHMARKS.md`).
- The UI stays usable during scans; scans can be paused, limited to a time window and throttled.
- Failures, skipped files, incomplete AD resolution and stale scans are always reported, never shown as clean.
- Installs work offline, and upgrades take a backup first.
