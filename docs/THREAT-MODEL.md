# Threat model

Covers the API, the worker and its share/folder scanner, PostgreSQL and the browser UI. Each milestone adds its own section. `03-SECURITY-AND-PRIVACY.md` holds the full product requirements.

## Assets
- Inventory of servers and scan jobs (later: findings, ACLs, who can reach sensitive data).
- Admin audit log.
- Database credentials and, from M1, the read-only scanning account.

## Trust boundaries
1. Browser to API (HTTPS in production, Windows sign-in).
2. API and worker to PostgreSQL (local or LAN).
3. Worker to file servers and AD (from M1; read-only).

## Threats and controls

| Threat | Control in M0 | Gap / later |
|---|---|---|
| Unauthenticated access | Fallback policy requires a signed-in user on every endpoint; only `/health` and static UI files are anonymous. Tested (401). | |
| Privilege misuse | Role policies per endpoint, roles from AD group SIDs. Tested per role (403). | Scoped data-owner access (M4). |
| Dev sign-in left on in production | `Auth:Mode=Development` throws at startup unless the environment is Development. | |
| Hiding admin actions | Audit rows written in the same transaction as the change; trigger blocks UPDATE/DELETE/TRUNCATE; hash chain detects edits and gaps. Tested. | A database superuser can disable the trigger and rewrite the chain. Signed off-box export in M5. |
| Injection through server names | Strict host name parser (no IPs, ports, UNC, paths); EF parameterises all SQL, including the two raw statements (queue claim, audit lock), which use interpolated parameters. | |
| XSS and clickjacking | CSP `default-src 'self'`, `frame-ancestors 'none'`, `nosniff`, no third-party scripts or fonts. React escapes output. Tested (headers). | `style-src 'unsafe-inline'` remains for the UI; tighten in M5. |
| Secrets in the repo | Connection strings only from user secrets, environment or the installed settings file; startup fails without one; `.gitignore` blocks key and env files. | |
| Leaking data in logs | Worker stores at most 500 characters of an error, without stack traces, in the job row. Content readers record fixed notes and the exception type only, never exception messages or file text (M2). | Full structured-log review in M5. |
| Runaway or stuck jobs | Leases expire and another worker reclaims the job; retry cap of 5 then Failed. Tested. | |
| Denial of service on list endpoints | Page size capped at 200. | Rate limiting in M5. |
| Supply chain | Pinned versions, lock files, `npm audit` on production dependencies in CI. | SBOM generation and signed releases in M5. Dev-tool advisories (Vite dev server, Vitest) need a major upgrade; they do not ship in the product. |
| Outbound data | No telemetry, no external calls at runtime, self-hosted fonts. `DOTNET_CLI_TELEMETRY_OPTOUT` in CI. | |

## Scanner (M1)

| Threat | Control | Gap / later |
|---|---|---|
| Scanner changes customer data | Lists directories only; never opens, writes, renames or deletes files; no API in the scanner writes to a share. Test checks file hashes and modified times before and after a scan. | Folder last-access times may update on Windows if last-access tracking is on. To be checked in the Windows lab (`WINDOWS-LAB.md`). |
| Loops or escaping the share through links | Junctions, symlinked folders and mount points are never followed; each is a recorded gap. Tested with a loop. | |
| Resource exhaustion (huge or deep trees) | Depth, per-folder and per-job limits; each produces a gap. Frontier lives in the database, not memory. Tested. | Throughput benchmarks in M5. |
| Malicious file names (control characters, invalid Unicode, path tricks) | Names are stored as data, never executed or used to build shell commands. Unpaired surrogates are replaced and flagged. Share names are validated before building UNC paths. The UI renders names as text (React escaping). | CSV/PDF export escaping in M5 (A19). |
| Over-privileged scan identity | Share listing uses `NetShareEnum` level 1, which needs no admin rights. We recommend a gMSA with Read only. No password is stored. | Preflight check that warns if the scan identity is an admin (M5). |
| Dev shortcut pointed at real data | `Scanner:LabRoot` (folder-based fake server) is refused unless the environment is Development. | |
| False "deleted" or "clean" | Removal needs a complete parent listing. Every unreadable or skipped area is a gap shown in the UI, and shares with gaps show as Partial, never Complete. | |

## Installed server (lab install)

| Threat | Control | Gap / later |
|---|---|---|
| Database password read from disk | Stored only in `%ProgramData%\DataScope\config\datascope.json`. The installer removes inherited permissions on that folder and grants only Administrators, SYSTEM, the API virtual account and the scan account, before the password is written. Setup writes through a temp file in the same folder, so it inherits that ACL. | Plain text inside the locked file; DPAPI encryption in M5. |
| Over-privileged database login | Setup creates a dedicated role with no SUPERUSER, CREATEDB or CREATEROLE, revokes PUBLIC access to the database, and refuses to reuse an existing superuser role. `check` fails if the configured login is a superuser. Tested. | Runtime role still owns the schema; split into migration owner and DML-only runtime role in M5. |
| PostgreSQL admin password exposure | Passed to Setup in a process-scoped environment variable or typed at a hidden prompt; never written to disk, never on a command line, removed from the environment right after. Install transcripts contain no secrets. | |
| Scan account password exposure | Set through `Win32_Service.Change`, so it is never on a command line; Windows keeps it as an LSA secret. A gMSA (recommended) has no password at all. | |
| Cross-site request forgery with ambient Windows sign-in | State-changing API calls need the `X-DataScope-Client` header; no CORS is enabled, so other origins can't add it. Tested for JSON and bodyless actions. | |
| Downgrade or HTTP/2 auth confusion | HTTPS only, HTTP/1.1 only (Negotiate needs it), HSTS outside Development. | Self-signed certificate in labs; customers should use their CA. |
| Tampered package | `SHA256SUMS` covers every program file and the installer checks it before changing anything; the zip has its own `.sha256`. | Authenticode-signed binaries and installer in M5 (needs a code-signing certificate). |
| Showing fake data as real | A 401 from a real server shows a sign-in help page, never the demo company. Users without a role see a no-access page. Tested. | |
| Uninstall destroying evidence | Uninstall never touches the database and keeps settings unless asked. Tested in CI. | |

## Classification (M2)

| Threat | Control | Gap / later |
|---|---|---|
| Scanner changes customer data while reading contents | Files are opened read-only with share-all access and never written, renamed or deleted. Tests check hashes and modified times before and after. | Last-access time can update on read when NTFS last-access tracking is on; check W13 in `WINDOWS-LAB.md`. |
| Reading triggers a cloud or tape recall | Offline, RecallOnOpen and RecallOnDataAccess files are skipped before opening, and Windows opens use `FILE_FLAG_OPEN_NO_RECALL`. Tested with attributes. | Real OneDrive / Azure File Sync / HSM behaviour is UNVERIFIED (W14). |
| Decompression bombs and huge files | Size caps before opening; zip entry count read from the directory first; every entry is capped at its declared size and a shared 256 MB budget; PDF streams are measured before decoding; text capped at 1,000,000 characters; 30 second timeout per file. Each limit gives TooLarge, Partial or TimedOut, never a silent pass. Tested with crafted bombs and lying headers. | |
| XML attacks (XXE, entity expansion) | DTDs prohibited, no resolver, values read in chunks. Tested. | |
| Parser bugs in malformed files | Every reader failure becomes Unreadable with a fixed note; one bad file never stops the scan. Tested with malformed zip, XML and PDF. | Parsers run in the worker process. A parser memory-safety bug would run with the scan account's rights. Process isolation is an open risk (D40). |
| Running content from files | Nothing found in a file, file name or path is ever executed, evaluated or used to build a command. Macros, scripts and embedded objects are treated as text or ignored. | |
| Over-claiming sensitivity | Results are "possible matches" with a confidence and a rule. Checksums (Luhn, SSN ranges, ABA) and context words cut false alarms; placeholders are ignored. Files not read stay Unknown, never Public. Synthetic corpus test: recall 98% or more, false alarms 2% or less. | Tuning against real customer document mixes needs the lab. |
| Sensitive values stored in the database | Evidence is a short redacted snippet: digits become `#` except the last 4 of an SSN or card, and secrets are fully masked. Full values are never stored. | Encrypting evidence at rest is deferred to M5 (D44). |
| Wrong people seeing evidence | Snippets need the `ReadEvidence` policy (Admin, Security analyst, Auditor); Viewers see counts and labels only. Tested per role. | Data-owner scoping in M4. |
| Hiding a label change | Overrides need `ReviewClassification`, require a reason and write a hash-chained audit entry in the same transaction. Overrides are never applied automatically to new findings, and the UI flags files that changed since review. Tested. | |

## Directory and permissions (M3)

| Threat | Control | Gap / later |
|---|---|---|
| Changing AD or permissions by mistake | The LDAP reader only binds and searches; the security reader only calls read APIs (`GetFileSecurityW`, `NetShareGetInfo`, `NetLocalGroupEnum`, `NetLocalGroupGetMembers`, `LookupAccountNameW`). No code path writes to AD, ACLs or group membership. | |
| Credentials on the wire | Windows: Negotiate with signing and sealing as the service account, no stored password. A password bind is refused without LDAPS outside Development. | LDAPS certificate pinning (M5). |
| Malicious or damaged security descriptors | Own parser with hard size caps and bounds checks; fuzzed with 5,000 corruptions per run; failures recorded as "Unreadable". Tested. | |
| Huge directories or groups | Paged LDAP search, ranged member retrieval, 500,000-object cap (a capped sync is marked truncated), 2-minute request timeout. Group loops detected with an iterative algorithm (no recursion). Tested against Samba with a 1,600-member group and a loop. | Benchmarks with 100,000 users (M5). |
| False certainty about access | Ranges with certainty; unresolved members, unread local groups, unread share permissions and conditional entries widen the range; every result says it is computed, not a Windows check. Tested. | |
| Directory data exposure | Directory and access views need a Data Scope role; requesting a sync needs PlatformAdmin and is audited. Data stays in the local database. | Data owners see only their shares (M4). |
| Stale data | Each answer names its directory snapshot and permission read time; the UI flags directory data older than two days and failed syncs. Failed syncs never replace the last good snapshot. Tested. | |
| Resource use in the API | Graphs and results cached with size limits; exposure checks at most 20,000 folders and reach 50,000 boundaries per request, and both say when they were cut. | Precomputed matrix for very large estates (D57). |

## Owners and reviews (M4)

| Threat | Control | Gap / later |
|---|---|---|
| A manager sees another manager's reviews | `/my/*` is scoped on the server by the caller's SID (owner, delegate, escalated backup); any other review is "not found". Tested with a second manager and a reader role. | |
| A forwarded or phished email approves something | Emails carry no token and no action links, only the portal address; decisions need a Windows sign-in. The body says so. Tested. | |
| Decisions or frozen evidence changed after the fact | Frozen entries, people, decisions and histories are append-only (database triggers reject UPDATE, DELETE and TRUNCATE; tested). Each review has a SHA-256 seal that the verify endpoint and exports recompute. Every create, decision batch, submit, delegation, close, and export is in the hash-chained audit log. | A database superuser can drop triggers; the seal and audit chain make that detectable, not impossible (as D10). |
| Changing AD or permissions by mistake | No code writes to AD, ACLs or group membership. Decisions are only recorded; people make any change. Tested end to end: the lab directory file is byte-identical after a full review. | |
| Races between people deciding | Every change locks the review row (`FOR UPDATE`); nothing can be decided after submission or campaign close. Tested. | |
| SMTP credentials or mail content exposure | Mail goes only to the customer's relay over STARTTLS (required outside Development). An optional SMTP password lives in the locked-down config file (D28). Logs record the error type and SMTP status, never the body. | DPAPI for the SMTP password (M5, with the connection string). |
| Directory enumeration through the owner portal | `/my/people` answers only owners and returns names of enabled users, at most 20, for searches of 2 to 100 characters. | |
| Spreadsheet formula injection in exports | Cells that start with `=`, `+`, `-`, `@`, tab, CR, `|` or `%` are prefixed with an apostrophe (numbers excepted). | |
| Large campaigns exhausting the API | At most 500 reviews per campaign, 2,000 listed people per entry (counts stay complete), 500 decisions per request; exports stream one review at a time. | |

## Reports and overview (M5a)

| Threat | Control | Gap / later |
|---|---|---|
| A role reads a report it should not | Each report checks its own policy on the server; the report list is filtered the same way. Viewers get no review evidence; people with no role get no reports or dashboard. Tested. | |
| Formula or script injection from file names or directory values | CSV cells go through the formula-safe writer; report pages HTML-encode every value, carry no scripts and load nothing external, under the same CSP as the app. Tested with hostile file names. | |
| Sensitive values leaking through reports | The sensitive data report lists categories, rule IDs and match counts only, never matched text or the masked snippets. Tested: no SSN digits or nearby names. | |
| Reports misread as complete or certain | Every report opens with scope, freshness, coverage, gaps, policy version and unresolved access, then its limitations. Access is shown as a range with certainty. | |
| Huge reports exhausting the API | Row limits (100,000 CSV, 5,000 page) and a 50,000-place limit, each stated in the report when hit. | Background jobs for very large estates. |
| Exports leaving no trace | Every export is written to the hash-chained audit log with who, which report, format, scope and row count. Tested. | |

## Hardening (M5b)

| Threat | Control | Gap / later |
|---|---|---|
| Passwords read from a copied settings file or a backup of the config folder | Database, SMTP and directory passwords and the backup key are DPAPI-encrypted for this machine (D78). `check` warns about any plain value. Tested with a fake cipher on Linux and real DPAPI on Windows CI. | Any program already running as an administrator or service on the server can still decrypt them; folder permissions still matter. |
| A compromised service rewriting the audit log, review history or schema | The services' login cannot change the schema, truncate, write the migrations table, or update or delete audit and review-event rows (D79). Enforced by PostgreSQL grants; tested by attempting each as that login. | The owner login can; it is only in the admin-only `setup-only` folder. |
| A runaway script or stolen session exhausting the API | Per-user and per-address rate limits, stricter for report exports (D80). Tested. | |
| Downgrade, script injection and cached responses | TLS 1.2+ only; CSP with no inline code or styles and `object-src 'none'`; report pages allow only their own hashed stylesheet; API responses `no-store` (D81). The UI was checked under the policy with no violations. | TLS 1.3 negotiation on Windows Server 2022 and later is UNVERIFIED (W34). |
| Unbounded growth of history | Daily retention in batches with minimum periods; evidence, labels and the audit log are never purged; each purge is audited (D83). Tested. | |
| A backup file read, changed or swapped | AES-256-GCM with per-chunk authentication bound to the header and position; a wrong key is named by id; a damaged or truncated file is refused before anything is changed (D84). Backups are written through a temporary file into an admin-only folder. Tested by flipping bytes and cutting files. | Anyone with both a backup and the key can read it; the key must be kept like a password. |
| Losing the server and with it every backup | The key can be shown and kept elsewhere; a new server restores with it (D85). Tested end to end on Linux. | Restoring on a new Windows server is UNVERIFIED (W33). |
| A restore that loses data or evidence silently | Whole-file check first; refuses newer schemas and open connections; safety backup first; single-transaction restore; audit entry with the count of audit entries now only in the safety copy (D87). Tested. | |
| Password exposure through backup tools | pg_dump and pg_restore get the password in their environment only, with inherited PG* variables cleared; error output is cut to its last 2,000 characters. | |

## Packaging and pilot (M5c)

| Threat | Control | Gap / later |
|---|---|---|
| A tampered or substituted package (a swapped DLL, an edited script) | Every Data Scope program and script is Authenticode-signed with one certificate, and the installer refuses a signed package where any of them is unsigned or signed by another certificate, even when `SHA256SUMS` was edited to match; `-RequireSignature` also refuses an unsigned package (D91). CI signs with a throwaway certificate and checks a swapped DLL is refused. | The first real certificate, time stamping and AllSigned execution policy are UNVERIFIED (W36). Third-party files are trusted on their publishers' signatures and the hash list. |
| A vulnerable or copyleft dependency shipped unknowingly | A CycloneDX SBOM of what actually ships, with hashes and licenses, in every package; the build fails on a denied license, a NuGet package with a known vulnerability or a high-severity npm advisory (D90). | Advisories published after a release reach customers only through a new release (patch policy in `SECURITY.md`). |
| Customer data leaking through support | The support bundle holds counts, versions and settings with every secret removed; error text is filtered for paths, accounts, addresses and long numbers; it is written locally and never sent (D93). Tested with synthetic names, an SSN and passwords that must not appear. | A message format the filter doesn't recognise could still carry a name; customers are told to read the bundle before sharing it. |
| A half-finished install on a customer's server | Every check that changes nothing runs first, and the installer stops before changing anything if one fails (D92). | |
| A scan overloading a production file server | Folder and file-read throttles with pause and resume (D94); the scale benchmark in CI catches regressions that would make scans or pages slow down as data grows (D95). | Real SMB load is UNVERIFIED (W39). |


## File activity (M6a)

| Threat | Control | Gap / later |
|---|---|---|
| A crafted event (from a file name or a hostile process) attacking the parser | Event XML is parsed with DTDs prohibited, no resolver, a 64 KB document cap and at most 64 data fields; control characters are removed from every value; parse errors never quote the content. An event that fails stays as Unreadable with its raw text (capped) and is counted, so nothing is silently dropped. Tests cover entity expansion, external entities, oversized, truncated and damaged events. | |
| Event text treated as an instruction | Paths, process names and share names from events are stored and shown as text only; nothing in an event is ever run, opened or looked up outside the database. | |
| Someone rewriting the audit trail | Stored events cannot be changed: a trigger refuses any update except the three columns Data Scope fills in later, and the services' login has column-level UPDATE on only those and no DELETE (D98). Tests check both the owner and the services' login are refused. Log clears (1102) are stored and shown. | Retention purges only through an owner function with a 30-day floor (M6c, D106). Gaps in collection are detected in M6b. |
| Overclaiming: an access check shown as a read | Each event carries its certainty; 5145 is "Access check only" and hidden by default, 4656 is "Request", and the daily chart counts only recorded operations (D97). | Windows records what was asked of a handle, not every byte read; this is stated on the screen. |
| A delete attached to the wrong file | A 4660 takes its path only from an earlier event on the same handle, logon session and process that asked for DELETE (D100). | |
| Activity seen by people who shouldn't | Viewers can't read activity; changing collection or a share's folder needs ManagePlatform and is audited (D101). | |
| A flood of events exhausting the server | Reads are batched (500) with a cap per pass (20,000), sources are leased so two workers never read the same log, and the lab reader refuses files over 256 MB. | Volume tests and rate limits against a real busy file server are M6b. |

## Collection from Windows (M6b)

| Threat | Control | Gap / later |
|---|---|---|
| The scan account used to read file servers' logs is abused | It needs only Event Log Readers on each file server (read the logs, nothing else), not administrator rights; the CI install test reads the log as a non-admin account (D102). | Event Log Readers can read every log on the server, not only Security. Windows has no narrower right. |
| Data Scope changing a customer's audit or firewall settings | It never does; the customer's administrator runs `Enable-DataScopeAuditing.ps1`, signed, with `-WhatIf` and `-Check`, and can undo it with `-Remove` (D103). | |
| A firewall opened wider than needed | The script enables only the built-in Remote Event Log Management rules and suggests limiting them to the Data Scope server's address. | Limiting the address is left to the admin. |
| Someone hiding activity by clearing or flooding the log | Clears (1102) are stored as events; records that disappear before they are read, and logs that start again, are stored as append-only gaps and shown on the screen and in the weekly summary (D102). The screen warns when more than half the log is unread. | Data Scope can say events were lost, not what they were. Gaps and clears raise alerts (M6c). |
| A malicious or broken file server hanging or flooding the collector | Host names are validated before use; reads time out; each pass is capped and can be paced; events from the server are parsed with the hardened parser (M6a). | |
| Credentials for reading logs | None stored: the scanner service's own Windows identity (ideally a gMSA) is used. | |

## Alerts, retention and activity reports (M6c)

| Threat | Control | Gap / later |
|---|---|---|
| Ransomware or an insider deleting files, or someone opening up permissions, going unnoticed | Mass delete, many refused attempts, permission and owner changes, cleared logs and missed events raise alerts on the File activity screen; High alerts are emailed (D105). | Alerts arrive after the next collection pass (every minute by default), not in real time, and Data Scope cannot stop the activity. |
| Someone hiding activity by acknowledging its alert | Acknowledging needs PlatformAdmin or SecurityAnalyst and is audited with the note; more activity reopens the alert; the events themselves can't be changed (D98). | |
| Using retention to erase recent evidence | The services' login cannot delete events or gaps; it can only call the purge function, which refuses a cutoff less than 30 days ago and deletes nothing newer than the cutoff. Each purge is audited with counts (D106). Tests cover the refusal and the audit entry. | Someone with the database owner's login can still delete anything; that login is kept in an Administrators-only folder (D79). |
| A flood of alerts or emails | One burst is one alert, and later events extend it; each alert is emailed once per address, through the outbox with its retries and limits. | |
| Alert emails disclosing more than needed | An email names the rule, the person Windows recorded, counts, the server and times, with a link to Data Scope. It holds no file contents, paths or matched text, and goes only to the configured addresses through the customer's own mail server. | |
| Activity reports overclaiming | Reports list only recorded operations and requests, never access checks, and open with which servers were collecting and what was missed in the period (D107). | |

## Lab feedback (M7)

| Threat | Control | Gap / later |
|---|---|---|
| A wrong owner chosen from a misleading suggestion (stale or incorrect `manager` data in AD) | Suggestions are labelled with where they came from (Manager, Windows owner, Direct rights) and why, and nothing is assigned without an administrator choosing it; assigning is audited and keeps history. | Data Scope can't tell if AD reporting lines are out of date. |
| A reporting loop or a huge group slowing the API | Chains stop at 32 levels or the first repeat; groups over 2,000 people and Everyone-like groups are skipped; tests cover a loop. | |
| Reporting lines exposed to people who shouldn't see them | "Reports to" and suggestions are only in screens that need ReadPosture (PlatformAdmin, SecurityAnalyst, Auditor, Viewer); owners in the review portal don't see them. | |
| The anonymous sign-in probe disclosing information | `GET /api/v1/sign-in` returns only the product name and "Windows"; no version, host, customer or data. Every other endpoint still needs sign-in (default-deny fallback policy). A test checks it sends no sign-in challenge and that `/me` still refuses anonymous callers. | |
| Someone mistaking the demo company for real data | The demo opens only when chosen on the sign-in page, every screen carries a banner saying it is made up, and a refused or failed sign-in never falls back to it (D111). | |
| A tampered or wrong package installed by the update command | `Update-DataScope.ps1` checks the zip against its SHA-256 and that its name and `VERSION` agree, runs only `Install-DataScope.ps1` from inside it, and passes `-RequireSignature` through to the installer's Authenticode check. Downloads come only from the named repository's green CI runs, as the signed-in admin. | The checksum comes from the same CI run as the zip, so it catches corruption, not a compromised build; signing (M5c) is the control for that. |
| The update command becoming a way in | It is run by an administrator on purpose; there is no updater service, schedule or listening port, and Data Scope stores no GitHub token (the GitHub CLI keeps its own sign-in for that admin). | Lab and pilot use; customers install signed releases by hand. |
| An admin hiding someone's access from review by putting them on the no-decision list | Only PlatformAdmins change the list, each entry needs a reason, and adding or removing is audited. The list in force is frozen into each review's seal, auditors and analysts still see listed entries marked "Managed by IT", and the certification report labels them (D110). | Owners do not see listed entries by design; auditors should review the list itself. |
| An admin hiding a server, or its evidence, by removing it | Removal needs PlatformAdmin and the anti-forgery header, is audited with what it changed, and deletes nothing: scans, findings, reviews, collected activity and the audit log stay, and adding the name again restores the server (D109). Removed servers stay listed on **Servers & scans**. | Removal stops future collection on that server; the audit entry is the record that it happened. |
| A scan writing to a server after it was removed | Removal is refused while a scan runs, cancels queued and paused scans in the same transaction, and a worker that claims a scan of a removed server cancels it before reading anything. Tests cover each. | |
| The Risks screen leaking secrets it found | Risks name the folder and how many files; never the matched text or file contents (an API test checks a synthetic password never appears). | |
| Someone reading the risk list as proof of misuse, or as complete | Every risk says it is computed from permissions, not evidence anyone opened a file; the screen says when the scan and directory sync ran, and lists where a check stopped early and shares the scan could not finish as risks of their own. | |
| A slow risk check on a large estate | The check reuses the bounded exposure query (20,000 folders) and permission walk (50,000 places) and says when it hit either limit; the list returns at most 2,000 risks with counts for all. | The check runs per request; caching or a nightly run may be needed at scale. |
| A risk accepted or snoozed to hide it rather than deal with it | Only administrators and analysts can do it; accepting needs a reason and ends within a year, snoozing within 90 days; both are audited with who, when and why, and stay as history after they end; the Accepted and Snoozed views show them with counts on the main list. A risk that becomes more severe shows again. | An administrator can keep re-accepting a risk; the audit log and history show each time. |
| A risk task closed without a real fix | The person who sent it cannot mark it fixed; it is verified only when the risk check no longer finds the risk in data gathered after the fix was recorded, and only a verified task can be closed. | Since D116 the API checks fixed tasks every 5 minutes once fresher data exists; with schedules off, a task waits for the next manual scan or sync. A risk fixed differently (for example, the files moved) may get a new id and need a fresh decision. |

## Scheduled scans (M8a)

| Threat | Control | Gap / later |
|---|---|---|
| Scans loading file servers during business hours | Scheduled scans start at an hour the admin picks (1 AM by default) and pause at a stop hour (7 AM by default), continuing the next night; a scan more than 6 hours late waits for the next time instead of starting in the day. D94's throttles still apply. | A scan someone starts by hand ignores the stop hour, on purpose; the admin can pause it. |
| Someone changing a schedule to stop scans, hiding new findings | Only PlatformAdmins can change a schedule, with the anti-forgery header; each change is audited with before and after; **Servers & scans** shows each server's schedule, when it next scans and any scheduled time that was missed. | Scans can still be turned off; the audit log and the screen are the record. |
| Two scanners, or a restart, starting the same scan twice | Each scheduled time is dealt with once under a row lock (`FOR UPDATE SKIP LOCKED`) and recorded on the server; no scan is started while one is queued, running or paused, or when one already ran since that time. Tests cover each. | |
| A scheduled scan reading data it shouldn't | A scheduled scan is the same read-only scan as a manual one, with the same scan account and limits; it is recorded as requested by "scheduler" so it is never mistaken for a person. | |
| Hourly scans loading a file server all day (D121) | Hourly is chosen per server by a PlatformAdmin (audited) and can be limited to a window with an **Until** hour; a scan still running at the next start is never doubled; each scan opens only new or changed files, so a repeat scan walks folders and permissions without re-reading contents; D94's throttles still apply. | A large share scanned every hour runs nearly all the time. The form shows the day's start times; there is no automatic load limit beyond the throttles. |
| An automatic fix check closing a task wrongly | The automatic check uses the same rule as **Check now**: only data gathered after the fix was recorded counts, and it is audited as "Data Scope (risk check)". It never closes a task; a person still does. | |
| A risk task closed without a real fix | The person who sent it cannot mark it fixed; it is verified only when the risk check no longer finds the risk in data gathered after the fix was recorded, and only a verified task can be closed. | Verification is manual (Check now); a task can sit in "waiting for a check" until someone checks. A risk fixed differently (for example, the files moved) may get a new id and need a fresh decision. |
## Stale and duplicate data (M8d)
| Someone deleting files because Data Scope called them stale, when people still read them | Data Scope never moves or deletes a file. Stale means nobody changed the file and file activity, where collected, shows nobody opened it; the screen and reports say per server whether activity is collected, and say files only read look stale where it isn't. Every screen and report says to check with the owner and retention policy first. | Activity is kept 365 days by default, so a 5 or 7 year threshold can't see reads older than that. |
| Treating likely copies as identical and deleting the wrong one | They are called likely copies everywhere; matching is by name and size only, and the screen shows each location and whether modified times match. | Content hashing to confirm copies is a later phase. |
| File names and paths shown to more people | The screen and API need ReadPosture, the same as the Shares and Sensitive data screens, which already show these paths; owners in the review portal don't see it. No file contents or matched text appear. | |
| A heavy query on a large estate | Lists are paged (at most 200 rows), the folder list stops at 50 and each duplicate shows at most 20 locations; duplicate grouping uses a partial index on current files' size and name. Reports page through groups. | Totals are worked out per request; a nightly summary may be needed at very large scale. |
| A file name or path running as a command | Names are only displayed and compared as text; nothing is opened or run. | |

## More file types (M8e)

| Threat | Control | Gap / later |
|---|---|---|
| A zip bomb or a zip of zips exhausting memory, disk or time | Nothing is extracted to disk. The entry count is checked before the zip is opened; each entry is read only within its type's size limit and never past its declared size; at most 256 MB is decompressed per zip; text entries are read only up to the text limit; zips and emails inside each other stop at 3 deep; the 30-second per-file limit covers the whole zip. Tests cover each. | Office and PDF files inside a zip each have their own decompression cap, so a zip of many small Office bombs is bounded by the time limit rather than a shared byte budget. |
| A crafted email exhausting the parser | Emails over 50 MB are not opened; MIME nesting stops at 20 levels and attachments count towards the 3-deep limit; header lines and counts are capped; HTML is stripped with time-limited patterns. Tests cover nesting bombs and broken messages. | |
| A password-protected zip or attachment reported as clean | Encrypted entries are counted and never opened; a zip of only encrypted files is reported as Encrypted, and a mixed one as Partial with the count. | |
| A misleading name hiding a file's real type (`payroll.xlsx.bak`, `export.tmp`) | Copies are read by their first bytes, not their names; binary copies are reported as not read, never as clean. | Other extensions (for example `.dat`) are still not read. |
| An email causing outbound requests or code to run | Links and remote images are never fetched, scripts are removed before scanning, attachments are only read as data. | |
| Email addresses and routing headers ending up in findings | Only the subject and bodies are scanned; addresses and other headers are left out. | |

## System settings (M9a)

| Threat | Control | Gap / later |
|---|---|---|
| Someone weakening Data Scope through its settings, for example turning alerts off or shortening history to destroy evidence | Only Platform admins can change settings, with the anti-forgery header; Auditors can only read. Every save and reset is audited with before and after values. Turning alerts or risk checks off, emptying the mail server and lowering history kept are warned about before saving. The audit log, access reviews, decisions and classification overrides are never purged, whatever the settings. | A Platform admin can still turn alerts off or keep 30 days of history; the audit log is the record. |
| The settings screen reaching security configuration | Only keys on a fixed list can be saved; anything else is refused. Role groups, the database connection, HTTPS, the mail server's sign-in, password and STARTTLS, content-reader safety limits, scanner internals, directory connection settings and rate limits are not on it. Tests try each kind. | |
| A row written straight into `system_settings` (for example by someone with database access) changing behaviour | The services apply only listed keys whose values pass the same checks as the screen; anything else is ignored and logged. | Someone with database write access can still set listed settings to valid values; protect the database as in "Installed server". |
| A crafted value breaking something downstream: header injection in email, a link to another site in emails, an extreme number | Every value is checked by type: whole numbers within limits, email addresses without spaces, angle brackets, commas or line breaks, an https:// address without sign-in, query or fragment, a mail server name without spaces, slashes or ports, choices from a list, at most 50 distinct alert addresses. The services check again when they read them and keep the last good values if a check fails. | |
| The test email used to send mail elsewhere | It goes only through the mail server already saved, to one address, with fixed text; only Platform admins can send it, and each one is audited. It uses the same outbox as other email. | An admin could send repeated test emails; each is audited. |
| Settings shown to more people | The screen and API need the Platform admin or Auditor role. None of the settings is a secret: the mail server sign-in is never shown, only whether one is set. | |

## Scan scope and exclusions (M9b)

| Threat | Control | Gap / later |
|---|---|---|
| An admin (or someone using an admin's session) hiding sensitive data from Data Scope with an exclusion | Exclusions never make data count as scanned or clean: a skipped folder is a coverage gap that keeps the share Partial and raises the NotCovered risk; unread contents are shown as Excluded with sensitivity Unknown and counted as not read. Every exclusion has a reason, shows who added it, and every add, change, turn on or off and removal is audited. Only Platform admins can change scope, with the anti-forgery header. | A Platform admin can still exclude data with a confirmation; the audit log and the coverage strip are the record. A second approver is not in V1. |
| Hiding a large part of the estate by accident | The screen previews what the last scans found before saving. Exclusions covering 10,000 files or more, a quarter of a share (or of all files for a type), any Restricted or Confidential files, or common document types need an explicit confirmation, checked again by the server, and again when one is turned back on. Leaving a whole share out always needs a reason and, from the screen, a confirmation. Only one folder (and below) per exclusion; no wildcards. | Many small exclusions can add up; the coverage strip shows the totals. |
| A crafted path escaping the share or matching more than intended (`..`, drive letters, UNC paths, wildcards, trailing spaces or dots that Windows ignores, SQL `LIKE` wildcards) | Paths are normalised and refused if they contain any of these; matching is on whole path segments without case, and `LIKE` patterns are escaped. Tests try each. | |
| Turning an exclusion off or removing it to make old results reappear as current | Files a newly added exclusion covers are re-evaluated in the next scan; turning one off lets the next scan read them again. Nothing is restored from before. | |
| Exclusions read by more people | Reading scope needs the Platform admin or Auditor role. Reasons are free text written by admins; they are shown as text, never as HTML. | |

## Sensitive Data Exposure Map (M9c)

| Threat | Control | Gap / later |
|---|---|---|
| The map gathering who can read which sensitive data in one place, useful to an attacker inside the network | It needs the same roles as Who has access (Platform admin, Security analyst, Auditor or Viewer); Data owners and anyone without a role are refused. It shows paths, counts, account and group names, never file contents or matched text. It is computed on request and stored nowhere. | Someone with a posture role sees the whole map; scoping it per owner is not in V1. |
| A reader trusting an uncertain answer, or taking a gap for safety | Folders whose permissions could not be read are Unknown and list no one; people who only might read are counted apart; uncertain tiles are striped and labelled; a box above the map lists every coverage limit (files not read or excluded, shares not fully scanned or left out, share permissions not read, disabled accounts, directory sync time). Breadth is drawn at worst, never narrower than it could be. | |
| Hiding a broad group by another entry: Everyone first, a group second | Every allow entry that lets an account read is listed, not only the first one Windows uses, so removing one is never shown as removing access. | |
| A write-only drop box shown as open, or an open folder missed | Reading is judged by the read bit on the computed access, tested for write-only entries. | |
| Heavy requests slowing the server | Each distinct permission set is evaluated once and reuses the access caches; at most 20,000 sensitive folders are evaluated and the response is capped (500 folders, 100 groups, 200 people). Requests are rate limited like the rest of the API. | Benchmarked with a synthetic directory: about 0.3 s at 2,000 users and 1.9 s at 20,000, close to linear in users (BENCHMARKS.md). Very large directories (100,000 users and up) are not yet measured. |
| Acting on the map changes permissions | It is read-only. People make any fix; Data Scope never changes permissions. | |

## Compliance standards (M10a)

| Threat | Control | Gap / later |
|---|---|---|
| A reader taking a tag or count as proof of compliance, or no tag as proof the data isn't there | The screen and API say it shows evidence and gaps, never compliance; each standard lists what Data Scope can't detect; files not read are counted and linked, never treated as clean; tags come from possible matches at Medium or High confidence. | Detection limits in DETECTION-LIMITATIONS.md still apply. |
| Someone quietly narrowing a standard so less data counts | Only Platform admins change settings; every change needs a reason and is audited with the before and after; the screen shows "Changed from the defaults" and the history. | No second-person approval. |
| Gathering where regulated data sits in one place | Same roles as Sensitive data and the Exposure map (Platform admin, Security analyst, Auditor, Viewer); Data owners get 403; history needs audit log access. Nothing leaves the server. | |
| Bad or hostile input in settings or filters | Standard ids come from a fixed catalog (unknown is 404 or 400); categories and confidence are parsed against fixed lists; reasons are 3 to 500 characters of plain text and shown as text; a database check limits categories to 1 to 16. | |
| Changes to files or permissions | Standards only tag and filter; nothing about files, permissions or AD changes. | |

## Compliance evidence report (M10c)

| Threat | Control | Gap / later |
|---|---|---|
| A reader, or an assessor, taking the report as a compliance verdict | The first limitation says Data Scope never says data or an organisation is compliant; matches are called possible matches; each thing the standard covers that Data Scope can't detect is listed; files not read are counted by reason and never treated as clean; access is labelled computed, with Possibly and Unknown where it could not be sure. | |
| One file gathering where regulated data sits, who can read it and which folders nobody reviewed | Only Platform admin, Security analyst and Auditor (the roles that see access reviews); Viewers and Data owners get 403. Rate limited like other reports, and each export is audited with the standard and scope. No matched values or surrounding text are in it, and it is made on the server and downloaded by the person who asked; nothing is sent anywhere. | A downloaded report is outside Data Scope's control; the install guide says to store it like other sensitive reports. |
| Hostile names in folders, groups or settings reasons reaching a spreadsheet or browser | The same writers as every report: CSV cells that start with = + - or @ are prefixed; the printable page is escaped text under a Content Security Policy that runs no script. | |
| A standard chosen to hide data, or a turned-off standard used to look clean | The standard must be turned on; a missing, unknown or turned-off one is refused with a 400 rather than an empty report; who turned it on, when and why is in the report. | |

## Risks per standard (M10b)

| Threat | Control | Gap / later |
|---|---|---|
| A risk with no standard read as unimportant, or a raised one as proof of a violation | The raise is one fixed step and the reason names the standard and the severity before; tags are possible matches by the D126 rule; files not read stay a separate risk and gap. Risks never say anything is compliant or non-compliant. | |
| Turning a standard off to make risks look smaller, or to bring an accepted risk back under its acceptance | Only Platform admins change standards, each change needs a reason and is audited (D126). Turning one on brings back risks accepted at a lower severity; turning one off only restores the severity they were accepted at. | No second-person approval for turning a standard off. |
| The standard filter used to probe for data | Same roles and policy as the Risks screen (Platform admin, Security analyst, Auditor, Viewer); an unknown or turned-off standard is a 400 from a fixed catalog. Risks carry counts and paths, never matched values. | |
| Heavy checks on large file servers | The new review rule checks at most 20,000 folders with data under a standard and says so when it stops; the other rules keep their limits. | |
| Changes to files, permissions or AD | Read-only: risks are worked out from stored data on each request; nothing is changed. | |

## More kinds of personal data (M11d)

| Threat | Control | Gap / later |
|---|---|---|
| Evidence becoming a copy of the personal data it found | Values are masked (two characters of a licence or passport number, the last four of an IBAN with its bank code hidden, "[name] + [street address]" for a person); digits around every match are replaced as before, and for personal details the other people's names and addresses in the snippet are hidden too. | Words around a match that are not digits or another person's record (a city name, a job title) can still appear, as for every rule. |
| A hostile file making the new patterns slow | Every pattern uses the non-backtracking engine; the name-to-address search is bounded to 200 characters after each label; records are sorted once. A 1,000,000-character hostile input is tested. | |
| False alarms from numbers with no checksum | Licence and passport numbers count only next to a label or under a column header; runs and repeated digits are skipped; a reviewer can label a file Internal, which also takes it out of every standard. | Look-alikes next to a label ("DL: 2024-117") are still possible matches. |
| An upgrade quietly widening a standard someone narrowed | Only a saved list equal to an earlier version's defaults gets today's defaults; any list an administrator changed is left alone. | Someone who deliberately chose exactly the old defaults gets the two new kinds and must remove them. |

## Bulletproof scan engine (M11a)

| Threat | Control | Gap / later |
|---|---|---|
| A hostile or damaged file crashes a parser, exhausts memory or loops, stopping the scan or the worker | Files are read in separate reader processes: a managed memory cap everywhere and, on Windows, a job object capping all memory, blocking child processes and killing the process with the worker; a hard time limit with kill; one retry, then Unreadable; the scan carries on. Tested with crashing, hanging and memory-hungry files. | Reader processes run as the worker's account: a parser exploit could read what the scan account can. A restricted token or AppContainer is later work. |
| A compromised reader process lies to the worker or floods it | Messages are length-prefixed and capped both ways (64 KB requests; responses up to the text limit); answers are checked for a known state, text within `MaxChars` and a short note; anything else is treated as unreadable. The process is sent only file paths the walk found. | |
| Sensitive data hidden by a misleading name (`payroll.jpg`, `customers.dat`, no extension) | Every file is read by its contents unless its name says program, disk image, video, audio or font; images are opened to check. | A document renamed as a program or video is still skipped by name, to keep scans fast. |
| Binary data scanned as text produces false findings | Binary formats are recognised by their first bytes and named; unknown binary data (undecodable, or more than one control character in ten) is reported as not read. | |
| Reading more files slows the file server | Programs, media and disk images are still never opened; images are opened only for their first bytes; the read rate limit (D123) applies to every opened file. | |

## Text in images (M11b)

| Threat | Control | Gap / later |
|---|---|---|
| A crafted image exploits an image decoder and runs code as the scan account | On Windows, images are decoded only by Windows' own decoders (Windows Imaging Component), patched by Windows Update; the older image libraries compiled into the bundled Leptonica are never given a customer's file. Decoding and recognition run in the reader process, with its memory cap and job object (D127). | The reader process runs as the scan account, which can read every share it scans: a successful exploit could read what the scan reads. A reader process with fewer rights (restricted token or AppContainer) is the D127 follow-up. |
| A decompression bomb: a small image claiming billions of pixels | The size is read from the header and anything over `MaxImagePixels` (50 million) is refused before decoding (tested with a 100,000 × 100,000 pixel PNG); files over `MaxImageBytes` are never opened; PDF pictures go through the PDF's decompression budget and pixel limit. | A later page of a multi-page TIFF is checked after its header is read, on Linux after decoding; the job object's memory cap still holds. |
| An image built to make recognition run for ever | All pictures in a file share `OcrTimeout`, enforced inside Tesseract per word; the reader process is killed at the file time limit plus 15 seconds whatever it is doing. | |
| The bundled native libraries are swapped or tampered with | Their SHA-256 hashes are pinned in the build, which fails if they differ; the package's own hashes and signature cover them at install (D89); the SBOM lists them with their hashes. They load only by full path from the install folder, never from the search path. | They are a third-party build (TesseractOCR package), not built by Data Scope from source. |
| OCR text is wrong, and a finding is trusted or a number missed | Every OCR result's note says it was machine-read, with the average confidence; findings in it are the same "possible matches" as any other. | A misread digit or a stray space (a tilted scan read `512-48-3307` as `5 12-48-3307`) hides a number. The known-answer set (M11e) measures how often. |
| Text read from images leaks | OCR text is handled exactly like other file text: only redacted evidence is stored, never the text itself, and nothing leaves the server. | |

## Quick and full scans (M11f)

| Threat | Control | Gap / later |
|---|---|---|
| A full scan overloads a file server or runs into the working day | A scheduled full scan keeps to the server's scan window: paused at the stop hour, it continues the next night. `Scanner:MaxFilesReadPerSecond` applies to full scans too. Only one scan per server runs at a time. | With quick scans set to by hand, a scheduled full scan runs until done. |
| The larger full-scan limits let a hostile file exhaust the worker | Every limit stays bounded and validated (3 minutes per file, 2 GB per reader process, 512 MB unpacked per file); full scans read in their own reader processes, killed at the file time limit plus 15 seconds, and the Windows job object caps their memory. The protections not about depth (characters scanned, pixel limit, nesting) are unchanged. | A server with little memory may need `Scanner:Content:Full:ReaderMemoryBytes` lowered. |
| Someone without rights starts full scans to load a file server | Starting any scan needs the ManagePlatform role and the anti-forgery header, as before; the audit log records each start with its kind (tested). | |
| A full scan is believed to have read everything | Files it still can't read keep their state (too large, timed out, partly read, not read) and stay listed under Not read; nothing unread is counted as clean. | |

## Crooked scans and pictures in Office files (M11g)

| Threat | Control | Gap / later |
|---|---|---|
| A picture built to make straightening slow or exhaust memory | Straightening works on a copy shrunk to at most 1,000 pixels a side and at most 100,000 ink pixels per angle, at about 50 angles across and 50 down; the levelled copy is at most about 1.5 times the recognised picture, which is itself capped at 12 million pixels. It runs in the reader process, inside its memory cap and time limit (D127). | |
| Second readings make one file take much longer | A second reading happens only for pictures that look like text and read poorly, and only within the file's shared OCR time (D129), which stops short of its time limit; the text read so far is kept. | Image-heavy shares of poor scans spend that time more often; the scan's progress and BENCHMARKS.md show the cost. |
| A crafted Office file hides a decompression bomb or a huge picture in its media folder | Each picture is checked against `MaxImageBytes` and the document's remaining unpacked-bytes budget from the zip's directory before it is opened, and its unpacked bytes are counted as they are read, so a picture lying about its size stops at the size it claimed; the pixel limit is read from its header before decoding; at most 50 pictures per file. | |
| A picture in an Office file exploits an image decoder | Pictures from Office files take exactly the path an image on a share takes: Windows' own decoders in the reader process (D129). Embedded objects (`word/embeddings`) and vector drawings (EMF, WMF) are never opened. | As for images on shares: the reader process runs as the scan account. |
| A wrong turn or a misread picture hides a number, or a guess is trusted | The reading kept is chosen by Tesseract's own confidence over the letters it read, not by the turn's guess; every OCR result's note says it was machine-read, with its confidence; a picture not read makes the document Partial with the count and reason, never Scanned. | Steeper angles than 15° and pages with text running several ways are not straightened. |

## Older Office files, Outlook messages, 7-Zip and RAR (M11c)

| Threat | Control | Gap / later |
|---|---|---|
| A crafted compound file (.doc, .xls, .ppt, .msg) makes the reader loop, recurse or read out of bounds | Data Scope's own reader: every sector number is checked against the file, each chain is followed at most once per sector, the entry tree is walked visiting each entry once, no recursion anywhere (PowerPoint records use an explicit stack, depth 32), every record length is checked against what holds it, and every read is bounds-checked by .NET, which turns a damaged structure into Unreadable (tested with a sector loop, a cut-off file and records running past the end). | |
| A compound file or archive exhausts memory | No stream is read past `MaxUncompressedBytes`; archives are capped in entries and unpacked bytes, each file stops at its declared size, and skipped files in solid archives count towards the cap. All of it runs in the reader process with its memory cap and time limit (D127). | A 7-Zip or RAR archive can declare a very large decompression dictionary, which SharpCompress allocates before the first byte: the reader process's memory cap stops it, the file is retried once and then marked Unreadable. |
| A bug in SharpCompress is exploited by a crafted archive | SharpCompress is managed code with no native parts, so memory corruption is not the usual risk; it runs only in the reader process; the version is pinned in the lock files and listed in the SBOM. Nothing is ever extracted to disk, so path traversal in archive names does not apply. | Its RAR and 7-Zip decoders are a large surface Data Scope did not write: security fixes come from updating the pinned version. |
| Macros or embedded objects in older Office files run | Nothing is executed: only the streams that hold text are parsed. Macros (VBA storages), embedded OLE objects and pictures are never opened, and formulas are never evaluated. | |
| Addresses from Outlook messages are stored | Sender and recipient properties are never read, as for `.eml`; only the subject, body and attachments are scanned, and only redacted evidence is stored. | |

## Outlook data files (M11c2)

| Threat | Control | Gap / later |
|---|---|---|
| A crafted .pst or .ost makes the reader loop, recurse or read out of bounds | Data Scope's own reader: every page and block is checked against the file's length, its own identifier, its signature and its checksum; every B-tree is walked without recursion, visiting each page once, with levels counting down and at most 8; data and subnode trees at most two levels, each block used once; heap references checked against their page's allocation map; property trees at most 4 levels with each record visited once. A damaged structure becomes a damaged message (counted, the rest read) or a damaged tree (what was read is kept). Tested with a tree that loops, a level that lies, a bad checksum, a header pointing past any file, a file cut short and 3,000 randomly damaged files. | |
| A mailbox exhausts memory or time | A message's own data is at most 4 MB and 10,000 properties; a compressed block inflates into a buffer of exactly its declared size (at most 64 KB) and more data is damage; attachments share `MaxUncompressedBytes`; text stops at the mailbox budget (at most 50,000,000 characters); reading stops at two thirds of the file's time limit. All of it runs in the reader process with its memory cap and time limit (D127). | A mailbox larger than a scan can read is reported Partial each time; it is never read whole in pieces across scans. |
| A mailbox password or encryption is broken | Outlook's .pst password is not encryption (the data is stored the same way) and Outlook's encodings need no key; Data Scope never tries a password. Files encrypted by Windows Information Protection are reported Encrypted and not read. | |
| Mail contents, addresses or attachments leave the server or reach the database | Mail is read in the reader process like any file; only redacted evidence is stored. Sender and recipient addresses are never read. Attachments are read in memory and never written to disk; links in messages and links to files elsewhere are never followed; nothing in a message is run. | Message subjects can be personal; they are scanned, never stored. |
| A large mailbox's text slows classification | Text over 1,000,000 characters is scanned in pieces of at most 1,000,000 characters, each with the same linear-time detectors, up to 50,000,000 characters. | A full-scan mailbox can take twenty times as long to classify as the largest other file. |

## Custom detection (M11h)

| Threat | Control | Gap / later |
|---|---|---|
| An administrator's pattern stalls or slows every scan (regular expression denial of service) | Patterns run on .NET's non-backtracking engine, so matching time grows with the text's length, never exponentially; look-around, back-references, atomic groups, oversized automata and patterns that match nothing are refused when saved; text is searched in 4,096-character pieces and a match is at most 256 characters; at most 25 rules are on; values counted per rule are capped at 10,000. Scanning stops at the first 1,000,000 characters of a file, as for the built-in detectors. Patterns that are slow on long runs are tested on a million characters (about 100 ms each in development; the test fails above 5 seconds). | Custom rules run in the worker, not in the reader processes, so they are bounded by the text cap and the engine rather than the per-file time limit; twenty-five slow patterns can still make classification several times slower, and the page does not yet show each rule's cost. |
| Someone without the right adds or weakens a rule, for example to hide data from reports | Only Platform admins can add, change, turn off or remove rules (tested: security analysts, auditors, viewers and data owners are refused, and signed-out callers get nothing); every change needs a reason and is audited with before and after. | |
| A rule's words reveal what they protect (a project code name, a merger) | The full rules are readable only by admins and auditors; Sensitive data and file detail show the rule's name, never its words or pattern; evidence is masked like any other finding. | An administrator may name a rule after what it protects; the page asks for a neutral name only by example. |
| Matched values leak through the test box or logs | The test box runs on text the administrator types, is not stored, not audited and not logged; only masked values are returned. Scan logs carry rule ids and counts, never matched text. | |
| A rule's match is taken as proof | Every custom finding is a possible match with the confidence its effect sets; "just list" findings are Low and never raise a label or count towards a compliance standard. | |

## Search box (M12a)

| Threat | Control | Gap / later |
|---|---|---|
| Someone without posture rights finds file or folder names, people or groups through the search | The search needs the same ReadPosture role as Shares, Sensitive data and Who has access; data owners and anonymous callers are refused (tested). Results carry only names, paths, titles and labels those screens already show, never contents or matched text. | Searches are not audited, like other reads. |
| A crafted search injects SQL, wildcards or regular expressions | Searches are parameters, never SQL text; `%`, `_` and backslashes are escaped for LIKE and every regular expression character is escaped for folder names (tested). Searches are 2 to 200 printable characters. | |
| Searching slows the database for everyone | Trigram indexes on current file and folder names; at most 50 candidates per kind; each kind is cancelled after 3 seconds and reported as left out. The person's API rate limit applies, and the box waits for a pause in typing and cancels the search it replaces. | |

## Find a person (M14a)

| Threat | Control | Gap / later |
|---|---|---|
| Someone uses the search to snoop on a colleague | Only platform admins can search (tested: security analysts, auditors, viewers and data owners are refused); every search needs a reason and is written to the hash-chained audit log, before it runs, with who searched and every name and address searched for (tested). | A platform admin can still search anyone; the audit log is what makes that visible. |
| A broad term turns the search into a dump of everything | Names need 3 or more characters with letters, addresses must be plain addresses (no wildcards, display names or lists; tested), at most 5 of each; LIKE wildcards in a term are escaped; each part lists at most 500 and says when there were more. | A common name still matches many files; the page suggests longer names. |
| The search slows the database for everyone | Each part has its own 20-second limit and is left out, marked, when it runs over; file names use the existing trigram index and activity the existing per-person indexes. | Scan evidence and folder names are searched without an index. |
| The results become another copy of the person's data | Nothing is stored but the audit entry; results go to the admin's browser only, and the download is made there with a file name that doesn't name the person. The search reads only what Data Scope already holds: names, sharing, ownership, the directory, activity and redacted evidence (D36), never file contents. | The downloaded file is the admin's to protect. |
| The results claim more than Data Scope knows | Each part says what it covers; scan evidence is described as text near a flagged match, never as every mention; the page and download say file contents were not searched. | Step 2 (a search scan) is needed to find every mention. |

## Detection test pack (M11e)

| Threat | Control | Gap / later |
|---|---|---|
| Real personal data gets into the pack, the repository or a results file | Every file is generated from made-up values (SSNs in the 512-48-33xx test range, issuers' test card numbers, published IBAN examples, invented names and addresses); no customer or real document is used. The results file holds only the pack's file names and Data Scope's masked results. | Anyone changing `make-test-pack.py` must keep to made-up values; review catches it. |
| The pack's hostile files harm the lab or the scanner (a zip bomb, programs, damaged files) | The zip bomb is 1 MB that unpacks to 1 GB of one repeated letter, and the programs are a few bytes with an MZ header and no code; nothing in the pack runs. The point is that the scanner stays within its limits on them, and CI checks that it does, on Linux and Windows. | Antivirus may still quarantine a file; the check then reports it missing rather than passing. |
| The lab scripts change more than they should | `Install-DataScopeTestPack.ps1` writes only inside its `-Path` folder, refuses a folder that does not hold the pack, needs `-Force` to replace one, and creates or reuses only the share it is given, read-only to the scan account. `Test-DataScopeTestPack.ps1` only reads, except that `-StartScan` queues one scan, sending the anti-forgery header like the UI. Both are lab-only and never in the product package. | |
| A passing pack is taken as proof that detection is complete | `expected.csv` lists what is checked and marks known gaps; DETECTION-LIMITATIONS.md stays the description of what is not found. | Detection rates at scale are M11e2. |

## Friendlier first install (M11i)

| Threat | Control | Gap / later |
|---|---|---|
| The domain script changes more of AD than the admin expects | It adds only a group, a KDS root key and a gMSA, each only when missing, looks up every name before changing anything, supports `-WhatIf`, and never removes or changes other objects; an existing gMSA of the same name only gains the server in who may use it. It runs only when a domain admin runs it; the product itself still never writes to AD. | An admin who runs it with `-Lab` in production gets a KDS root key that is usable before it has replicated, which can break gMSAs on other domain controllers for a while; the parameter's help and the guide say lab only. |
| The scan account is too powerful | The gMSA is created in no group, with no rights, usable only by the Data Scope server and with AES Kerberos only; the installer's existing check that the scanner is not LocalSystem stays. | The script does not check an existing account's group memberships. |
| A typed answer installs with the wrong accounts | Answers must name an account or group that exists; the checks then show both, and the installer asks once more before changing anything. | |
| A tampered launcher in a signed package | No unsigned launcher is shipped: every script in the package is signed and checked. | |


## System health (M13o, D168)

| Threat | Control | Residual |
|---|---|---|
| The page tells an outsider about the server (paths, versions, machine names) | Only admins and auditors can read it; the anti-forgery header and sign-in rules are the same as every other page. It shows drive letters, free space, the database version and service machine names, never file contents, findings or secrets. | An auditor sees the server's drive letters and versions; they already see the audit log. |
| A check runs something or changes the server | Every check is a read: the database, the audit log, free space on Data Scope's own drives and the certificate stores. Nothing is started, restarted or deleted; the page only names the fix. | None. |
| A forged heartbeat hides a stopped service | Heartbeats are written by the services' own database login; nobody can write them through the API. | Someone with the database login can write anything; that login is protected as in the database section. |
| The page itself is slow or fails on a broken part | Each check catches its own failure and shows it as Unknown with the reason, so one broken part never hides the others. | None. |

## One-file setup wizard and bundled PostgreSQL (M15a, D169)

| Threat | Control | Residual |
|---|---|---|
| A tampered DataScope-Setup.exe or package inside it | A signed release signs the setup program after the zip is appended, so Authenticode covers the zip too; a signed wizard runs the installer with `-RequireSignature`, which checks every Data Scope program and script against the package's publisher and SHA256SUMS. | Lab builds from CI are unsigned: only the `.sha256` file guards them, which catches damage, not a compromised build. |
| A crafted zip writes outside the temporary folder (zip slip) | Every entry's full path must stay inside the folder, or unpacking stops (tested). | None. |
| Passwords seen by other programs on the server | The scan and PostgreSQL admin passwords go to the one installer process in environment variables, never on a command line (which any user can list); the script reads and removes them at once. Quiet mode takes them from the environment too, and setup's log never holds them (CI checks). | An administrator on the server can read a running process's environment; administrators already control the server. |
| The bundled database reachable from the network | It listens on `localhost` only, with SCRAM passwords; Windows Firewall gets no rule for it. | Software already running on the server can reach it, with a password. |
| The database files read by an ordinary user | The data folder is open to Administrators, SYSTEM and Network Service only (inheritance removed; CI checks the exact list). | Other services that run as Network Service could read it; PostgreSQL's own installer makes the same choice. A virtual service account is a possible hardening. |
| The `postgres` admin password leaked or lost | Generated (24 random bytes), saved with DPAPI (machine scope, Data Scope's entropy) in `config\setup-only`, which only Administrators and SYSTEM can open, before `initdb` uses it. The services never use it. | An administrator can decrypt it, by design (needed for later admin work and reinstalls). |
| A swapped Visual C++ runtime installer | The installer runs `vc_redist.x64.exe` only when Microsoft's valid Authenticode signature is on it. | Microsoft's "latest" download address means the shipped version isn't pinned; the SBOM records its hash. |
| A swapped PostgreSQL download at build time | The build checks the EDB zip against the SHA-256 pinned in `scripts/windows-dependencies.env` and stops on any difference. | Trust in EDB's build itself. |
| An uninstall entry that runs something else | The Settings, Apps entry runs the signed copy of `Uninstall-DataScope.ps1` in `C:\Program Files\Data Scope`, which only administrators can change; the registry key is under HKLM. | None beyond administrators. |
| LGPL obligations for the bundled DLLs | libintl and libiconv ship unmodified as separate, replaceable files, with their licence and source location in THIRD-PARTY-NOTICES.md; the SBOM gate refuses any other LGPL component (owner's decision, D169). | Subject to the owner's legal review before the first paid sale. |
