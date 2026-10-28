# Try Data Scope on your Windows PC

This guide tests the UI, the backend and the M1 share and folder scanner. Allow about 30 minutes, most of it installing.

**Installing on a server in a test domain?** Use `docs/INSTALL-DATASCOPE.md` instead. It uses the ready-made install package and needs no developer tools.

**Just want to click around the UI?** Run only the web UI (`cd web`, `npm ci`, `npm run dev`): with no backend it shows a demo on made-up data for a company called Northwind.

## 1. Install the tools (once)

Open **PowerShell** and run:

```powershell
winget install Git.Git
winget install Microsoft.DotNet.SDK.10
winget install OpenJS.NodeJS.LTS
```

Then install **PostgreSQL 16** from https://www.postgresql.org/download/windows/ (the EDB installer).
- During setup it asks for a password for the `postgres` user. Pick one and remember it.
- Keep the default port 5432.
- You can skip Stack Builder at the end.

Close PowerShell and open a new window, so it picks up the new tools.

## 2. Get the code

```powershell
cd $HOME
git clone https://github.com/OpenSourceCybersecurity/OS-DataScope.git
cd OS-DataScope
```

## 3. Point Data Scope at your database

Replace `YOUR-PG-PASSWORD` with the password you chose:

```powershell
$cs = "Host=localhost;Database=datascope_dev;Username=postgres;Password=YOUR-PG-PASSWORD"
dotnet user-secrets --project src/DataScope.Api set ConnectionStrings:DataScope $cs
dotnet user-secrets --project src/DataScope.Worker set ConnectionStrings:DataScope $cs
dotnet tool restore
dotnet ef database update -p src/DataScope.Persistence --connection $cs
```

The last command ends with `Done.`. It creates the `datascope_dev` database and its tables. The password is saved in your Windows profile, not in the code.

## 4. Run the automated tests

```powershell
$env:DATASCOPE_TEST_CONNECTION = "Host=localhost;Database=postgres;Username=postgres;Password=YOUR-PG-PASSWORD"
dotnet test
npm --prefix web ci
npm --prefix web test
```

**Expected:**
- `dotnet test` prints four "Passed!" lines with 0 failed. The totals are 54 Core tests, 8 scanner tests, 47 setup tests and 53 API and worker tests.
- `npm test` reports `Tests 39 passed`.
- The tests create and delete their own throwaway databases. They never touch `datascope_dev`.

## 5. Make a fake file server

```powershell
node scripts/make-lab.mjs
```

This creates `lab-data\nw-fs01`, a pretend file server. It has shares `HR`, `Finance`, `Operations`, `Public` and the admin share `C$`, all filled with made-up files, including payroll workbooks, offer letters and invoices with fake SSNs, card numbers and dates of birth. It also adds one folder link that loops back on itself, so you can watch the scanner refuse to follow it.

## 6. Run the app

Build the UI once:

```powershell
npm --prefix web run build
```

**Window 1, the API.** This also serves the UI.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/DataScope.Api --urls http://127.0.0.1:5080
```

**Window 2, the scan worker.** Open a second PowerShell window in the same folder and run:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src/DataScope.Worker
```

## 7. Scan it in the UI

1. Open http://127.0.0.1:5080 and go to **Servers & scans**.
2. Click **Add file server**, type `nw-fs01` and click **Add server**. Typing `10.0.0.5` instead gives a plain-English error.
3. Click **Quick scan**. The card shows the scan running, then **Finished** after a few seconds. Clicking **Pause** while it runs pauses it, and **Resume** carries on.
4. Go to **Shares**. You should see:
   - **HR, Finance and Public:** marked **Complete**.
   - **Operations:** marked **Partial · 1 gap**. Click it, then open **Not covered** to see the link it refused to follow.
   - **C$:** listed as "Never (admin share)".
5. Click a share to browse its folders and files.
6. Untick **Scanned** on a share and scan again. That share keeps its old results; the others update.
7. Go to **Sensitive data**. HR payroll files and `fmla-request.docx` are **Restricted**, offer letters are **Confidential** (date of birth), and `Finance\AR\card-payments.txt` is Restricted. Click a file to see what was found, with numbers masked like `***-**-1234`.
8. In that panel, set a different label with a reason. The change and reason are written to the admin audit log; the Audit log screen is still the demo, so open http://127.0.0.1:5080/api/v1/audit-log to see the entry.
9. Choose **Not read**. `workpapers.zip` is listed as "Damaged or unsafe".

10. Go to **Who has access**. The strip at the top says the synthetic domain was synced (the worker does it on start). **Open to everyone** lists `HR\Benefits` first: it holds a Restricted medical form and gives Everyone read. `Finance` folders are **Possibly open**, because the lab pretends the scan account can't read Finance's share permissions. `Operations\IT` is **Unknown**, because its permissions are unreadable.
11. Click `HR\Benefits`, open **Any domain user** to see exactly which entry opens it, and switch to **Permission entries** to see every entry with its member count.
12. Choose **By person**, search `casey` and pick Casey Silva: she can read HR, but `HR\Employees` is missing because a deny entry blocks her there.
13. Back in **Sensitive data**, open `fmla-request.docx`. **Who can open this** shows that anyone in the domain can read it.

The other screens (Overview, Risks and so on) still show the Northwind demo, and a yellow banner says so.

## What you can't test yet

- **Real Windows sign-in.** In Development everyone is a fake admin. Signing in with your AD account (Kerberos) needs a domain-joined server and the production settings. This is UNVERIFIED.
- **Running as Windows services.** The code supports it, but there is no installer yet. The installer comes in M5. This is UNVERIFIED.
- **Scanning a real Windows file server.** The steps above use a fake server built from folders. Scanning real SMB shares as a service account is in `docs/WINDOWS-LAB.md`, and it is UNVERIFIED until that lab runs.
- **Reading contents over the network.** Here the worker reads local folders. Reading files on a real SMB share as the scan account, last-access times and offline or cloud-only files are UNVERIFIED until the Windows lab runs (`docs/WINDOWS-LAB.md`, W12 to W17).

## If something goes wrong

- **`dotnet` or `npm` is not recognized.** Open a new PowerShell window after installing.
- **`password authentication failed`.** The password in `$cs` doesn't match the one you set when installing PostgreSQL.
- **Port 5080 is in use.** Change `--urls` to another port, such as `http://127.0.0.1:5090`, and use that port in `$api` and in the browser.
