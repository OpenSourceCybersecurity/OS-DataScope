# Developing Data Scope

Everything here runs on one machine with no internet access after the first restore. Nothing in this repo phones home.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.100 or later 10.0.x | `global.json` pins the feature band. |
| Node.js | 22 LTS | For the web UI only. |
| PostgreSQL | 16 | Local install or `docker-compose.dev.yml`. |
| Windows | Windows 11 / Server 2022+ | Needed only for Windows auth and running as a service. Everything else builds and tests on Linux and macOS too. |

## 1. Database

Pick a local password and keep it out of the repo.

```bash
export DATASCOPE_DEV_DB_PASSWORD='choose-one'
docker compose -f docker-compose.dev.yml up -d
```

Or with a local PostgreSQL install: create a role `datascope` with `CREATEDB CREATEROLE` and a database `datascope_dev` owned by it. (The setup tool's tests create and drop throwaway login roles.)

## 2. Connection string (user secrets, never appsettings)

```bash
CS="Host=localhost;Database=datascope_dev;Username=datascope;Password=$DATASCOPE_DEV_DB_PASSWORD"
dotnet user-secrets --project src/DataScope.Api set ConnectionStrings:DataScope "$CS"
dotnet user-secrets --project src/DataScope.Worker set ConnectionStrings:DataScope "$CS"
```

User secrets live in your profile folder, not in the repo. The API and worker refuse to start without a connection string.

## 3. Schema

```bash
dotnet tool restore
dotnet ef database update -p src/DataScope.Persistence --connection "$CS"
```

Roll back everything: `dotnet ef database update 0 -p src/DataScope.Persistence --connection "$CS"`.

Add a migration after changing entities:

```bash
dotnet ef migrations add <Name> -p src/DataScope.Persistence
```

CI fails if the model and migrations drift apart. For an install, produce a self-contained migration bundle instead of shipping the SDK:

```bash
dotnet ef migrations bundle -p src/DataScope.Persistence -r win-x64 --self-contained -o artifacts/datascope-migrate.exe
```

## 4. Run

Make a synthetic file server to scan (made-up names and bytes only):

```bash
node scripts/make-lab.mjs          # writes lab-data/nw-fs01/{HR,Finance,Operations,Public,C$}
npm --prefix web ci && npm --prefix web run build     # the API serves web/dist in Development
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/DataScope.Api --urls http://127.0.0.1:5080
DOTNET_ENVIRONMENT=Development dotnet run --project src/DataScope.Worker
```

Open http://127.0.0.1:5080, go to **Servers & scans**, add `nw-fs01` and click **Quick scan**. The **Shares** screen then shows the four disk shares, `C$` marked as never scanned, and one coverage gap: a link inside `Operations\Projects` that the scanner refuses to follow. **Sensitive data** shows payroll workbooks and offer letters in HR, card and bank files in Finance, and a script with a password in Operations. `Finance\Audit\2024\workpapers.zip` is listed under **Not read** because it isn't a real zip.

How the pieces behave in Development:
- **Sign-in.** The API signs everyone in as a fixed fake admin (`Auth:Mode=Development`). It refuses to start with that mode in any other environment. Production uses Windows Negotiate (Kerberos/NTLM) and maps AD group SIDs to roles under `Auth:RoleGroupSids`.
- **File server.** The worker reads shares from `lab-data/<hostname>/` (`Scanner:LabRoot`) instead of calling Windows. `Scanner:LabRoot` is refused outside Development. On Windows without it, the worker lists real SMB shares with `NetShareEnum`.
- **Permissions and directory.** `make-lab.mjs` also writes `lab-data/nw-fs01.security.json` (share and NTFS permissions, local groups, one folder the scan account may not read) and `lab-data/northwind.test.directory.json` (a synthetic domain with nested groups, a disabled user, a group loop, a member from another forest and reporting lines for owner suggestions). The worker reads them instead of Windows and LDAP (`Directory:LabFile`, Development only) and syncs the directory on start.
- **File activity.** `make-lab.mjs` also writes `lab-data/nw-fs01.security-events.xml`, about 300 synthetic Security events over the last 7 days in the format `wevtutil qe Security /f:xml /e:Events` writes: reads and writes in Finance and HR, a contractor's denied access to HR Payroll, a permission change, 60 deletes in Operations today (enough for a mass delete alert), a backup agent outside any share and a cleared log. The worker's activity collector reads that file (`Activity:` `PollSeconds` 60, `BatchSize` 500, `MaxEventsPerPass` 20,000) once collection is turned on for the server on the **File activity** screen. On Windows outside Development the worker reads each file server's Security log remotely (D102); elsewhere there is no reader and the screen says so. `Activity:MaxEventsPerSecond` (0, no limit) paces reading and `Activity:ReadTimeoutSeconds` (120) bounds each read. After each pass the collector checks the alert rules (`Activity:Alerts`: `MassDeleteCount` 50 in `MassDeleteMinutes` 10, `DeniedCount` 20 in `DeniedMinutes` 10, `PermissionChangeMinutes` 60, `EmailTo` empty, `EmailMedium` false; D105); the lab data raises a mass delete, a permission change and a cleared-log alert. Events and gaps are purged after `Retention:ActivityDays` (365) through the `datascope_purge_activity` function (D106).
- **Screens.** Every screen shows live data from the API except **Risks**, which still shows the synthetic Northwind demo and says so in a banner.

Scan limits live in the worker's `appsettings.json` under `Scanner`: `MaxDepth` (64), `MaxEntriesPerFolder` (200,000), `MaxEntriesPerJob` (20,000,000), plus two throttles, `MaxFoldersPerSecond` and `MaxFilesReadPerSecond` (0, no limit). Anything cut off by a limit is recorded as a coverage gap, never silently dropped. Content-reading limits live under `Scanner:Content` (size caps, 1,000,000 characters, 200 PDF pages, 10,000 zip entries, 256 MB uncompressed, 100 MB zips, 50 MB emails, zips and emails 3 deep, 30 seconds per file, and `MaxMailboxChars` 5,000,000 characters for one Outlook .pst or .ost file, 20,000,000 in full scans); set `Enabled` to `false` for a metadata-only scan.

For UI work alone, `npm --prefix web run dev` runs the prototype on synthetic data without the backend.

## 5. Test

```bash
export DATASCOPE_TEST_CONNECTION="Host=localhost;Database=postgres;Username=datascope;Password=$DATASCOPE_DEV_DB_PASSWORD"
dotnet test --filter "Category!=Benchmark"    # all tests; each test class gets its own throwaway database
dotnet test --filter "Category!=Database&Category!=Directory&Category!=Benchmark"   # without PostgreSQL or Samba
dotnet test tests/DataScope.Api.Tests -c Release --filter Category=Benchmark   # scale benchmark, see BENCHMARKS.md
npm --prefix web test
dotnet format --verify-no-changes
```

Database tests fail loudly (they are not skipped) when `DATASCOPE_TEST_CONNECTION` is missing, so a green run always means they ran.

Directory tests read a real Active Directory schema from a throwaway Samba AD domain controller on loopback (Ubuntu 24.04, run as root; CI does the same):

```bash
sudo apt-get install -y samba samba-ad-dc samba-ad-provision ldb-tools
sudo tests/lab/samba-dc.sh            # NORTHWIND.TEST in /tmp/datascope-samba, synthetic users and groups only
export DATASCOPE_TEST_LDAP='Server=127.0.0.1;Port=389;User=Administrator@northwind.test;Password=Lab-only-Passw0rd!'
dotnet test --filter Category=Directory
```

They also fail loudly without `DATASCOPE_TEST_LDAP`. The script turns off LDAP signing on that test DC so a simple bind works; never do that on a real domain controller.

## Repository layout

| Path | What |
|---|---|
| `src/DataScope.Core` | Domain rules with no I/O: scan job state machine, host name validation, audit hash chain, roles. |
| `src/DataScope.Persistence` | EF Core model, migrations, audit log writer, Postgres job queue, inventory store. |
| `src/DataScope.Scanner` | Read-only file system and SMB access: share listing, single-folder listing, content readers, permission reads, and the file share connector a scan reads through (`Connectors/`, D141). |
| `src/DataScope.ActiveDirectory` | Read-only directory sync over LDAP (and the Development lab reader). |
| `src/DataScope.Api` | ASP.NET Core API (`/api/v1`), auth, RBAC, security headers, serves the UI. |
| `src/DataScope.Worker` | Background worker (Windows Service on Windows) that claims scan jobs and walks shares. |
| `src/DataScope.Setup` | Server setup tool the installer runs: database and login role, migrations, role mapping, HTTPS settings, `check`. |
| `deploy/windows` | `Install-DataScope.ps1`, `Uninstall-DataScope.ps1`, `Prepare-DataScopeDomain.ps1` (run on a domain controller before the first install), the CI-only lab scripts in `ci/`, and the test pack's lab scripts in `lab/`. |
| `scripts/package.sh` | Builds the Windows install zip (`artifacts/DataScope-<version>.zip`). |
| `tests/` | xUnit tests. `DataScope.Api.Tests` also covers the worker against a real database. |
| `web/` | React + TypeScript UI. Servers & scans and Shares use the API; other screens are still the synthetic demo. |
| `scripts/make-lab.mjs` | Generates a synthetic file server for development. |
| `tests/test-pack` | The detection test pack (D138): synthetic files and `expected.csv`, checked by `TestPackTests`. To change it, edit `scripts/make-test-pack.py` (needs LibreOffice, `python3 -m pip install py7zr` and zip), run it, then `DATASCOPE_WRITE_TEST_PACK=1 dotnet test tests/DataScope.Scanner.Tests --filter TestPackTests` for the Outlook and password-protected files, and commit the result. |
| `scripts/package-test-pack.sh` | Builds the test pack zip for a lab (`artifacts/DataScope-Test-Pack-<version>.zip`). |
