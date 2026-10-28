# Data Scope admin guide

Everything after the first install: optional settings, each screen in more depth, upgrades, backups and troubleshooting. To install, start with [INSTALL-DATASCOPE.md](INSTALL-DATASCOPE.md).

## Contents

- [Prepare the domain by hand](#prepare-the-domain-by-hand)
- [Install with the PowerShell script instead of the wizard](#install-with-the-powershell-script-instead-of-the-wizard)
- [Quiet install](#quiet-install)
- [Check detection with the test pack](#check-detection-with-the-test-pack)
- [Share permissions and local groups](#let-data-scope-see-share-permissions-and-local-groups)
- [Email for access reviews](#email-for-access-reviews)
- [Scan speed](#keep-scans-gentle-on-a-busy-file-server) and [schedules](#when-servers-are-scanned)
- [File activity](#file-activity) and [alerts](#alerts)
- [Stale and duplicate data](#stale-and-duplicate-data), [System settings](#system-settings), [Scan scope and exclusions](#scan-scope-and-exclusions), [Exposure map](#exposure-map), [Compliance standards](#compliance-standards), [Search](#search), [Text in images](#text-in-images)
- [Getting help without sharing data](#getting-help-without-sharing-data)
- [Upgrading](#upgrading)
- [Backups](#backups)
- [Uninstalling](#uninstalling)
- [When something goes wrong](#when-something-goes-wrong)

## Prepare the domain by hand

Without the script, or with a normal account rather than a gMSA, run this on dc01 as a domain admin:

```powershell
New-ADGroup -Name 'DataScope-Admins' -GroupScope Global -GroupCategory Security
Add-ADGroupMember -Identity 'DataScope-Admins' -Members 'luke'
# Lab only: lets gMSAs work straight away. In production, skip -EffectiveTime and wait 10 hours.
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))
New-ADServiceAccount -Name 'svc-datascope' -DNSHostName 'svc-datascope.yourdomain.local' -PrincipalsAllowedToRetrieveManagedPassword 'DS01$'
```

A normal scan account is a user such as `svc-datascope` with a long password that never expires; the installer asks for its password. Either way, the scan account must not be a domain admin or a member of any admin group.

## Quiet install

Server Core, or many servers: `DataScope-Setup-<version>.exe /quiet --scan-account YOURDOMAIN\svc-datascope$ --admin-group YOURDOMAIN\DataScope-Admins --log C:\Install\setup.log` installs with no windows (exit code 0 when it worked); `/?` lists every option. A scan account that isn't a gMSA takes its password from the environment variable `DATASCOPE_INSTALL_SCAN_PASSWORD`.

## Install with the PowerShell script instead of the wizard

Use this when you want to bring your own PostgreSQL or script the install. Download **DataScope-windows-package** from the same CI run, install PostgreSQL first (below), then unzip the package and run the installer, in PowerShell **as administrator**:

```powershell
cd C:\Install
(Get-FileHash .\DataScope-*.zip -Algorithm SHA256).Hash   # compare with the .sha256 file
Expand-Archive .\DataScope-*.zip -DestinationPath C:\Install
cd (Get-ChildItem C:\Install -Directory -Filter 'DataScope-*' | Select-Object -First 1).FullName
Get-ChildItem -Recurse | Unblock-File
Set-ExecutionPolicy -Scope Process Bypass -Force

.\Install-DataScope.ps1
```

It asks for the scan account and the admin group, suggesting `YOURDOMAIN\svc-datascope$` and `YOURDOMAIN\DataScope-Admins` when they exist (press Enter to take a suggestion), runs its checks, and asks once more before changing anything. To answer up front instead, as a script would:

```powershell
# Optional: run only the checks first. Nothing is changed.
.\Install-DataScope.ps1 -PreflightOnly -ScanAccount 'YOURDOMAIN\svc-datascope$' -AdminGroup 'YOURDOMAIN\DataScope-Admins'

.\Install-DataScope.ps1 -ScanAccount 'YOURDOMAIN\svc-datascope$' -AdminGroup 'YOURDOMAIN\DataScope-Admins'
```

With a gMSA, the installer checks that ds01 may use it before installing anything. It installs the AD PowerShell tools (RSAT-AD-PowerShell) to check when they are missing, refreshes ds01's sign-in if the first check fails, and if ds01 still may not use it, stops and prints the command to run on dc01.

The installer starts with checks: Windows version, the package's hashes and signatures, memory, disk, the HTTPS port, the certificate, the domain, the accounts and PostgreSQL. Each line says `[OK  ]`, `[WARN]` or `[FAIL]`; if anything fails it stops before changing anything and says what to fix. A package signed by the vendor shows `Package: signed by <vendor>`; packages built by CI for the lab are not signed and show a warning instead. Add `-RequireSignature` to refuse an unsigned package, as customers should.

The installer:
- asks for the PostgreSQL `postgres` password (and the scan account's password if it isn't a gMSA),
- installs two Windows services, **Data Scope API** and **Data Scope Scanner**,
- creates the `datascope` database with two logins: `datascope_owner`, which only setup uses to change the database, and `datascope_app` for the services, which can't change the database's structure or the audit log. Both get generated passwords, saved encrypted for this server only,
- creates a self-signed HTTPS certificate, and saves its public part as `C:\ProgramData\DataScope\datascope-https.cer` for browsers to trust,
- opens port 443 in Windows Firewall for domain and private networks,
- takes a first encrypted backup and schedules one every night (see [Backups](#backups)),
- prints the address to open when it's done.

It finishes with `Data Scope <version> is running.` and the next steps with your server's address filled in. A log of every step is saved in `C:\ProgramData\DataScope\logs`.

**Save the backup key now.** Backups are encrypted, and restoring one on another server needs this server's backup key. Show it and copy it into your password manager:

```powershell
& 'C:\Program Files\Data Scope\setup\DataScope.Setup.exe' backup-key
```

It is 64 characters like `3f9c0a1e-...`. Without a copy, losing ds01 means losing every backup.

**Script options:**
- `-CertificateThumbprint <thumbprint>` uses a certificate you already have in the computer's Personal store, such as one from your AD Certificate Services. Browsers then trust it with no extra step.
- `-ViewerGroup`, `-AuditorGroup` and `-AnalystGroup` map more AD groups to read-only roles.
- `-Port 8443` uses another port.
- `-DatabaseHost` points at PostgreSQL on another server. Install the PostgreSQL **Command Line Tools** on ds01 too (the same installer, with only that part selected) and pass `-PgBinPath 'C:\Program Files\PostgreSQL\16\bin'`.
- `-BackupTime 01:00` and `-BackupsToKeep 30` change the nightly backup. `-NoScheduledBackup` skips it if you back up PostgreSQL your own way.

### PostgreSQL for the script way

1. Download the Windows installer from https://www.postgresql.org/download/windows/ (EDB).
2. Keep the default port 5432. Choose a password for the `postgres` user and note it down; the Data Scope installer asks for it once.
3. Keep **Command Line Tools** selected: Data Scope's backups use its `pg_dump`.
4. Skip Stack Builder at the end.

## Check detection with the test pack

Each CI run also makes **DataScope-test-pack**: about 100 made-up files in every format Data Scope reads, with the answer for each. Copy it to fs01, and its `README.txt` walks you through sharing it and checking a scan in two commands; the check prints `ok` or `FAIL` for every file. It is for a lab only, and it changes nothing outside its own folder and share.

## Let Data Scope see share permissions and local groups

Windows only shows a share's own permissions (Advanced Sharing) and the file server's local groups (such as BUILTIN\Users) to administrators of that server. Without them, Data Scope still reads every folder's NTFS permissions, but marks results that depend on the share or local groups as **Partial** or **Possibly**. To get firm answers, add the scan account to fs01's local Administrators group:

```powershell
Add-LocalGroupMember -Group Administrators -Member 'YOURDOMAIN\svc-datascope$'
```

This is a trade-off: Data Scope itself never changes anything, but an administrator account is more valuable to an attacker. Many customers accept "Partial" instead.

## Email for access reviews

Access reviews work without email: owners open **My reviews** in Data Scope. To have Data Scope email owners when a review starts, remind them before it's due and escalate to the backup owner, point it at your own mail server. Data Scope only talks to the server you name, and STARTTLS is required.

The easy way: open **System settings** (under Settings), and under **Email** enter the mail server, port and From address, **Save**, then **Send test** to your own address. Nothing needs restarting. Or, on ds01, open `C:\ProgramData\DataScope\config\datascope.json` in Notepad as administrator and add a `Notifications` block next to the existing settings:

```json
"Notifications": {
  "PublicUrl": "https://ds01.yourdomain.local/",
  "Smtp": {
    "Host": "mail.yourdomain.local",
    "Port": 587,
    "From": "datascope@yourdomain.local",
    "UseStartTls": true
  }
}
```

If your relay needs a sign-in, add `"UserName"` inside `Smtp` in datascope.json (sign-ins are never set on System settings), and save the password with setup, which encrypts it (don't type it into the file):

```powershell
& 'C:\Program Files\Data Scope\setup\DataScope.Setup.exe' secret --name smtp-password
```

After editing datascope.json, restart the scanner service, which sends the email:

```powershell
Restart-Service DataScopeScanner
```

`PublicUrl` is the address people use in their browser; the email links to it. If email isn't set up, reminders are recorded and marked "not sent" so you can see what would have gone out.

## Keep scans gentle on a busy file server

Scans only read, but a first scan of a large server lists every folder and opens every supported file, which adds disk load. To spread it out, set **Folders listed per second** and **Files read per second** under **Advanced**, **Scanning** on **System settings**; they apply from the next scan, or the next time a paused scan resumes. Or open `C:\ProgramData\DataScope\config\datascope.json` in Notepad as administrator and add a `Scanner` block (or these two lines inside the existing one):

```json
"Scanner": {
  "MaxFoldersPerSecond": 50,
  "MaxFilesReadPerSecond": 20
}
```

`MaxFoldersPerSecond` limits how fast folders are listed; `MaxFilesReadPerSecond` limits how many files are opened for reading (files skipped by type or size don't count). 0 means no limit, which is the default. Then restart the scanner:

```powershell
Restart-Service DataScopeScanner
```

You can also pause a running scan from **Servers & scans** (or the share's page) in the web UI during business hours and resume it later; it carries on from where it stopped. [BENCHMARKS.md](BENCHMARKS.md) has measured unthrottled speeds to help pick numbers.

## When servers are scanned

A server you add is scanned every night at 1 AM, in the time zone of ds01, and a scan still running at 7 AM pauses and carries on the next night. To change it, open **Servers & scans**, choose **Change** next to the server's schedule, and pick **Hourly**, **Daily**, **Weekly** or **By hand**, then a start time (to the minute) and, if you like, a pause time. **Hourly** scans every 1, 2, 3, 4, 6, 8 or 12 hours from the start time; with an **Until** hour they run only in that window, for example every 2 hours from 7:45 AM until 7 PM. The form lists the times each day's scans start. A scan still running when the next one is due carries on and is never started twice, and each scan opens only files that are new or changed. Servers added before this version stay **Only by hand** until you set a schedule. To give servers you add from now on a different schedule, change it under **New servers** on **System settings**; servers already added keep their own. If ds01 was off at the scheduled time and more than 6 hours have passed, Data Scope waits for the next time and the server shows the scan it missed.

There are two kinds of scan. A **Quick scan** lists everything but opens only new and changed files, so it is fast; scheduled scans are quick scans. A **Full scan** opens every file again, changed or not, and gives each one more time and room: 3 minutes per file, 2 minutes to read the text in its pictures, 1,000 PDF pages, and bigger PDFs, Office files, emails and zips. It finds what a quick scan can't, such as the rest of a long scanned PDF or a file restored with an old date. Each server gets a full scan every Saturday at its scan start time, in place of that night's quick scan; servers already scanned on a schedule get the same when you upgrade, and servers scanned by hand don't. Change it with **Change** next to the schedule (**Weekly**, **Monthly** on the first of a weekday, or **By hand**), and for servers you add later under **New servers** on **System settings**. A full scan keeps to the pause time like any scheduled scan and carries on the next night until it is done, and no quick scan starts while it is under way. To run one now, choose **Full scan** on the server's card. On a big server a full scan can take many hours. Its limits can be changed under `"Content": { "Full": { ... } }` in the `Scanner` block of datascope.json, with the same names as the other content limits.

## File activity

**File activity** shows who read, changed, deleted or re-permissioned files, from each file server's Security log. Data Scope reads the log remotely over the network as its scan account; nothing is installed on the file server, and Data Scope never changes audit settings itself. Windows only writes these events once auditing is on, so an administrator does that first on each file server with the script that comes in the package.

1. On **fs01**, copy `Enable-DataScopeAuditing.ps1` (next to the setup file, or in the package folder), open PowerShell **as administrator** in that folder, and see what it would change:

   ```powershell
   .\Enable-DataScopeAuditing.ps1 -ReaderAccount 'YOURDOMAIN\svc-datascope$' -WhatIf
   ```

   By default it audits every disk share on the server; name folders with `-Path C:\Shares\HR, C:\Shares\Finance` to audit only those. It turns on the **File System** and **File Share** audit subcategories, adds audit entries for changes, deletes, permission changes, refused access and file reads to each folder, adds the scan account to **Event Log Readers** (enough to read the log, nothing more), allows **Remote Event Log Management** through the firewall, and makes the Security log at least 1 GB. Add `-NoReads` if you only want changes and deletes; reads are the bulk of the events.

2. Run it again without `-WhatIf`, then check:

   ```powershell
   .\Enable-DataScopeAuditing.ps1 -ReaderAccount 'YOURDOMAIN\svc-datascope$'
   .\Enable-DataScopeAuditing.ps1 -Check -ReaderAccount 'YOURDOMAIN\svc-datascope$'
   ```

   In a domain, Group Policy can put audit policy back at its next refresh. If `-Check` later shows **Audit File System** off, set **Audit File System** and **Audit File Share** (Success and Failure) in a GPO under *Computer Configuration > Policies > Windows Settings > Security Settings > Advanced Audit Policy Configuration > Object Access* instead.

3. In Data Scope, open **File activity** and choose **Turn on** next to fs01. It reads the log every minute. The first read takes everything the log still holds, so it can take a while on a busy server.

4. Open a file on `\\fs01\HR` from the client. Within a couple of minutes it shows as **Read**, **Recorded**. Share connections and share access checks (event 5145) are hidden unless you tick **Include share access checks and connections**, and are never counted as reads.

Under **Collection**, each server shows how far back its Security log goes. If collection falls more than half a log behind, or the log wraps or is cleared before Data Scope reads it, the screen says so and lists what was missed; those events are lost and Data Scope never guesses them. If Data Scope is installed on the file server itself, restart the scanner after step 2 (`Restart-Service DataScopeScanner`) so it picks up the new group.

To undo: `.\Enable-DataScopeAuditing.ps1 -Remove -ReaderAccount 'YOURDOMAIN\svc-datascope$'` takes out the audit entries and the group membership, and prints the commands to turn audit policy off.

### Alerts

Data Scope watches the collected events for a few patterns and shows them at the top of **File activity**: one person deleting 50 or more files within 10 minutes, 20 refused attempts within 10 minutes, any permission or owner change, the Security log being cleared, and events missed. Each alert says who, what, how many, where and when; **Show events** lists the events behind it. Anyone with the Platform admin or Security analyst role can **Acknowledge** an alert with a note (recorded in the audit log); if more happens afterwards, it opens again.

To have High alerts (mass deletes and cleared logs) emailed, set up email as in [Email for access reviews](#email-for-access-reviews), then add an `Activity` block to `C:\ProgramData\DataScope\config\datascope.json` and restart the scanner:

```json
"Activity": {
  "Alerts": {
    "EmailTo": [ "it-security@yourdomain.local" ],
    "EmailMedium": false
  }
}
```

You can set the same things under **File activity and alerts** on **System settings** instead, without a restart. Set `EmailMedium` to `true` to email permission changes, refused attempts and missed events too. The thresholds are settings in the same block (`MassDeleteCount` and `MassDeleteMinutes`, `DeniedCount` and `DeniedMinutes`, and `PermissionChangeMinutes`), and `"Enabled": false` turns alerts off. Collected events are kept for 365 days (`"Retention": { "ActivityDays": 365 }`, at least 30, 0 keeps them forever); alerts are kept.

**Reports** has two activity reports, **File activity** and **File activity alerts**, for the last 7, 30 or 90 days or 12 months.

## Stale and duplicate data

**Stale & duplicates** (under Data) shows, from the latest scan, files nobody has changed in years and likely copies of the same file in more than one place. Pick how old counts as stale (1 to 30 years, for long retention holds) and a share. Choose **Sensitive and stale** to list only the old Restricted and Confidential files. The **Stale** tab lists the shares and folders with the most old data and the largest or oldest files; the **Duplicates** tab lists files with the same name and size in several places, the ones taking the most space first. If [file activity](#file-activity) is collected for a server, files someone opened since the cutoff are left out; otherwise files people only open look stale too, and the screen says so.

Data Scope only reports: it never moves, archives or deletes anything. Shares holding old Restricted or Confidential files also show on **Risks** as "Sensitive files nobody has changed in years". **Reports** has **Stale data** and **Duplicate files** for the owners.

To change the defaults, use **Advanced**, **Stale and duplicate data** on **System settings** (it applies at once), or add this to `datascope.json` and restart the Data Scope service:

```json
"Cleanup": { "StaleAfterYears": 3, "MinDuplicateBytes": 1024 }
```

`StaleAfterYears` is 1 to 30; `MinDuplicateBytes` is the smallest file counted as a copy.

## System settings

**System settings** (under Settings) is one place for the settings you're likely to change. Up front are the three most people set once: **Email**, the schedule **New servers** get, and **File activity and alerts**. The rest suit most organisations at their defaults and are folded under **Advanced**: how hard scans may work, stale and duplicate thresholds, how long history is kept, how often Active Directory is read, and how often risks sent to IT are checked. **Advanced** says how many of them have been changed from the default. Platform admins can change them; Auditors can see them. Each group has its own **Save**, and changes apply within a minute without restarting anything.

Each setting says where its value comes from: **Built-in value**, **Set in datascope.json**, or who changed it here and when. A value saved here wins over datascope.json; **Reset** goes back to the datascope.json or built-in value. Every save, reset and test email is recorded in the audit log. Shortening how long history is kept, turning alerts or risk checks off, or emptying the mail server shows a warning first, because older history is deleted at the next clear-up.

Some settings are deliberately not on this screen: who has which role, the database, the HTTPS certificate, the mail server's sign-in, and the scanner's safety limits. Change those with the setup tool or datascope.json as described above. Each server's own schedule stays on **Servers & scans**.

## Scan scope and exclusions

**Scan scope & exclusions** (under Settings) shows what scans cover and what has been left out, and why. Leave out a whole share when it holds nothing you need to govern; unchecking a share on **Shares** brings you here to give a reason. To leave out only part of a share, add an exclusion:

- **A folder, Not scanned:** the folder and everything below it is never listed. Its files, permissions and sensitive data are not seen, and the share shows as partly covered.
- **A folder, Listed, contents not read:** files and permissions are listed as usual, but contents are never opened, so sensitive data there is not found.
- **A file type** (for example `.iso` or `.bak`): files of that type are listed with their permissions; only their contents are skipped.

Every exclusion needs a reason, and the screen shows what the last scan found there before you save. One that covers a lot (10,000 files, a quarter of a share, any Restricted or Confidential files, or a common document type such as .docx or .pdf) needs you to tick a confirmation. Changes apply from the next scan. Anything left out stays visible as a coverage gap on Shares, Risks and reports; it is never counted as clean. Turn an exclusion off to scan it again without losing its reason. Every change is in the audit log and in the History card.

## Exposure map

**Exposure map** (under Posture) shows where Restricted and Confidential files are and how widely they can be read. Each share is a box and each folder a tile, sized by its sensitive files and coloured by how many people can read them: red for anyone in the domain, then more than 25, 6 to 25 and 1 to 5 people. Striped tiles are not certain, and grey striped ones could not be checked at all. Select a tile for who can read it and through which groups, then a group or a person for what it opens and why.

The lists under the map answer the usual questions: **Folders** (most exposed first), **Groups** (which groups let the most people read sensitive files), **People** (who can read the most) and **Shares**. The box above the map always says what the map cannot see, such as files that were not read or share permissions Data Scope could not read. Treat those as unknown, not as safe. The map is worked out from permissions, the way Windows evaluates them; it does not show who actually opened anything (see **File activity** for that).

## Compliance standards

**Compliance standards** (under Settings) lets a Platform admin turn on the standards your organisation answers to: HIPAA, PCI DSS, GLBA, US state breach laws, or Passwords and keys. Each one counts certain kinds of data (for example PCI DSS counts payment card numbers); **Change** picks which kinds count and whether medium-confidence matches count. Every change asks why and is recorded in History.

For each standard that is on, the screen shows how many files, folders and shares hold that data. **See files** opens Sensitive data narrowed to the standard, and **See who can read them** opens the Exposure map narrowed to it. Files show the standards they fall under. A file a reviewer labelled Public or Internal is not counted.

This is evidence for your compliance work, not a compliance verdict. Each standard lists what Data Scope can't find (for example handwriting), and files that were not read are shown separately, never counted as clean.

For an assessor, each standard that is on has an **Evidence report** (printable page or CSV), also on **Reports** as **Compliance evidence**. It says when and why the standard was turned on, what counts, how complete the scans were, where that data is and how old it is, who can read each folder and through which groups, each folder's owner and whether their access review is up to date, and which requirements the evidence relates to. Admins, Security analysts and Auditors can open it. Store it like any other sensitive report.

On **Risks**, a risk about data a turned-on standard covers shows the standard's name and is one step more severe (Low becomes Medium, Medium becomes High), and its details say so. A new kind of risk, **Regulated data with an overdue access review**, appears when an owner hasn't reviewed a folder holding that data within their review frequency. Use the standard filter on Risks, or **See risks** on the standard's card here, to see only the risks for one standard. Turning a standard on can bring back a risk you accepted before, because it is now more severe.

## Search

The search box at the top of every page (Ctrl+K) finds people, groups, shares, folders, files and file servers by name. Paste a path such as `\\fs01\HR\Payroll` to jump to that folder. Pick a result to open the screen that explains it: a person's access, a folder's permissions, or a file's findings. Data owners don't see the box.

File and folder names are searched with PostgreSQL's `pg_trgm` extension, which is part of every standard PostgreSQL install, including the EDB installer for Windows. The upgrade adds it, and indexes the current file and folder names once; on millions of files that takes a few minutes.

## Text in images

Data Scope reads the text in images, scanned PDFs and faxes with OCR on the server (nothing is sent anywhere), so a scanned form or a screenshot with a Social Security number is found like any document. Each such file's note says its text was read by OCR, with how confident the reader was: OCR can misread a character, so check the file itself before acting on a finding.

OCR needs the **Microsoft Visual C++ Redistributable (2015-2022, x64)** on the Data Scope server. The EDB PostgreSQL installer usually installs it; the Data Scope installer's checks say whether it is there. If it isn't, install it as administrator and restart the scanner:

```powershell
Invoke-WebRequest https://aka.ms/vs/17/release/vc_redist.x64.exe -OutFile $env:TEMP\vc_redist.x64.exe
& $env:TEMP\vc_redist.x64.exe /install /quiet /norestart
Restart-Service DataScopeScanner
```

Until then, images and scanned PDFs are listed as **Not read**, with the reason, and are read on the next scan after it is installed.

Older Office files (.doc, .xls, .ppt), Outlook messages saved as .msg files, and 7-Zip and RAR archives are read too. Outlook mailbox files (.pst, and .ost offline copies) are read message by message, with their attachments; a very large mailbox is read in part, and a full scan reads more of it.

Pages scanned sideways, upside down or a little crooked are turned and straightened before they are read, and pictures pasted into Word, Excel and PowerPoint files (a scanned form in a document, a screenshot in a slide) are read too. A document whose pictures could not all be read is listed as partly read, with how many and why.

Recognition takes a fraction of a second for a screenshot or a form and a few seconds for a dense page, and the pictures in one file get 20 seconds in all, so a long scanned PDF may be only partly read by a quick scan. The weekly full scan gives each file 2 minutes and reads it further. To skip OCR, for example on a server full of photos, open `C:\ProgramData\DataScope\config\datascope.json` in Notepad as administrator, add `"Content": { "ReadTextInImages": false }` inside the `Scanner` block (add the block if there is none, as in [Keep scans gentle](#keep-scans-gentle-on-a-busy-file-server)) and run `Restart-Service DataScopeScanner`; images are then listed as not read. `OcrTimeout` and `PerFileTimeout` in the same place raise the time limits.

## Getting help without sharing data

If something goes wrong, make a support bundle on ds01, as administrator:

```powershell
& 'C:\Program Files\Data Scope\setup\DataScope.Setup.exe' support-bundle --include-event-log
```

It writes `datascope-support-<date>.zip` in the current folder (or `--out <folder or file>`): versions, the `check` results, settings with every password removed, counts of files, scans and gaps by state, recent error messages with paths and account names replaced, and the last week of Data Scope's event log entries. It contains no file names, server names, people or matched text, and nothing is sent anywhere. Open it and read it before you share it.

## Upgrading

### The quick way: Update-DataScope.ps1

One command on ds01 gets the newest build that passed every check, verifies it and upgrades in place. It needs the GitHub CLI, signed in as an account that can see the repository. Set that up once, in PowerShell as administrator:

```powershell
winget install --id GitHub.cli
# Close and reopen PowerShell as administrator so gh is on the PATH, then:
gh auth login      # GitHub.com, HTTPS, log in with a web browser
```

Then, whenever you want the latest changes, from the folder of any package you've installed before (for example `C:\Install\DataScope-<version>`):

```powershell
.\Update-DataScope.ps1
```

It finds the newest green CI build of `main`, downloads its package, checks the zip against its checksum, unzips it to `C:\ProgramData\DataScope\updates` and runs that package's `Install-DataScope.ps1`, which upgrades as described below. If that version is already installed it says so and stops.

- **Try a pull request before it's merged:** `.\Update-DataScope.ps1 -Branch risks` (the branch name is on the pull request page). Going back is just `.\Update-DataScope.ps1` again once it's merged, or `-Branch main -Force`.
- **See what it would install without changing anything:** add `-CheckOnly`.
- **Use a package you already downloaded:** `.\Update-DataScope.ps1 -Package C:\Install\DataScope-<version>.zip` (keep its `.sha256` file next to it). This needs no GitHub CLI.
- **Refuse unsigned packages:** add `-RequireSignature`. Lab builds from CI aren't code-signed, so only use it with signed packages.

The script runs only the installer from inside the checked zip. Builds are kept for 30 days, so after a long gap push a change or download the package by hand.

### By hand

Download the newer package, unzip it on ds01 the same way, and run the installer with no parameters:

```powershell
.\Install-DataScope.ps1
```

It stops the services, replaces the programs, backs up the database to `C:\ProgramData\DataScope\backups\datascope-before-upgrade-<date>.dsbackup`, updates the database and starts them again. Settings, scan results and the audit log are kept.

## Backups

Every night at 02:30 the task **Data Scope backup** (Task Scheduler, folder **Data Scope**) saves an encrypted copy of the database in `C:\ProgramData\DataScope\backups` and keeps the newest 14. Only administrators can open that folder. Copies taken before upgrades are never deleted automatically. Each backup is recorded in **Audit log**, and `DataScope.Setup check` warns when the newest one is more than two days old.

Backups are only on ds01's disk until you copy them somewhere else. Copy them to another server or to your usual backup system; they are encrypted, so that is safe as long as the backup key is kept separately.

In PowerShell as administrator, with `$setup = 'C:\Program Files\Data Scope\setup\DataScope.Setup.exe'`:

- **Back up now:** `& $setup backup`
- **Check a backup can be restored** (reads it end to end, changes nothing): `& $setup verify-backup --from <file>`
- **Restore a backup** (replaces everything in Data Scope with the backup's contents):
  ```powershell
  Stop-Service DataScopeScanner, DataScopeApi
  & $setup restore --from 'C:\ProgramData\DataScope\backups\datascope-<date>.dsbackup' --confirm
  Start-Service DataScopeApi, DataScopeScanner
  ```
  Before changing anything, restore checks the whole file and saves a copy of the current database as `datascope-before-restore-<date>.dsbackup`, so a restore can itself be undone. Anything recorded after the backup was made, including audit log entries, is replaced. Those entries stay in that copy, and the restore's own audit entry says how many there were. Keep that copy as long as you keep audit records.

### Rolling back an upgrade

Use the older package's own setup, because the newer one would update the restored database again:

```powershell
Stop-Service DataScopeScanner, DataScopeApi
cd C:\Install\DataScope-<older version>
.\setup\DataScope.Setup.exe restore --from 'C:\ProgramData\DataScope\backups\datascope-before-upgrade-<date>.dsbackup' --confirm --config C:\ProgramData\DataScope\config\datascope.json
.\Install-DataScope.ps1 -SkipUpgradeBackup
```

### Move to a new server

Saved passwords are encrypted for the server that saved them, so settings can't be copied to another server. Install fresh and bring the data across instead:

1. On the old server, take a backup (`& $setup backup`) and make sure you have its backup key (`& $setup backup-key`).
2. Install PostgreSQL and Data Scope on the new server as in [INSTALL-DATASCOPE.md](INSTALL-DATASCOPE.md), with the same groups.
3. Copy the `.dsbackup` file to the new server, then:
   ```powershell
   Stop-Service DataScopeScanner, DataScopeApi
   $env:DATASCOPE_BACKUP_KEY = Read-Host 'Old server''s backup key'
   & $setup restore --from C:\Install\datascope-<date>.dsbackup --confirm
   Remove-Item Env:\DATASCOPE_BACKUP_KEY
   Start-Service DataScopeApi, DataScopeScanner
   ```
4. Set the SMTP and directory passwords again with `& $setup secret --name smtp-password` (or `directory-password`) if you used them.

The new server keeps its own backup key for new backups: save that one too.

## Uninstalling

Open **Settings, Apps**, find **Data Scope** and choose **Uninstall** (it asks first). Or, in PowerShell as administrator:

```powershell
& 'C:\Program Files\Data Scope\Uninstall-DataScope.ps1'
```

This removes the services, the nightly backup task, the firewall rule and `C:\Program Files\Data Scope`. It never deletes the database or the backups. Settings, including the backup key, stay in `C:\ProgramData\DataScope\config` unless you add `-RemoveSettings`; save the backup key first if you do. The script prints the commands to drop the database if you really want it gone. When setup installed the **Data Scope Database**, it keeps running with your results and installing again uses it; `-RemoveDatabase` deletes it, its programs and all its data for good.

## When something goes wrong

- **Check the configuration.** This reads everything and changes nothing:
  ```powershell
  & 'C:\Program Files\Data Scope\setup\DataScope.Setup.exe' check
  ```
- **Read the logs.** Open **Event Viewer**, **Windows Logs**, **Application**, and look for the sources **Data Scope API** and **Data Scope Scanner**.
- **The installer said a port is in use.** Something else (often IIS) listens on 443. Stop it, or install with `-Port 8443`.
- **The scanner service won't start.** The scan account's password is wrong or it lacks "Log on as a service". If a domain GPO controls that right, add the account to the GPO.
- **"pg_dump was not found".** Install the PostgreSQL Command Line Tools on ds01 and run the installer again with `-PgBinPath`, or save the folder with `& $setup configure --pg-bin 'C:\Program Files\PostgreSQL\16\bin'`.
- **The Data Scope Database service won't start.** Its own log is in `C:\ProgramData\DataScope\database\log`. Setup's log (the window's details) is in `C:\ProgramData\DataScope\logs` too.
- **"The setting ... could not be decrypted".** The settings file came from another server. See [Move to a new server](#move-to-a-new-server).
- **The nightly backup failed.** Task Scheduler shows the task's last result. Run `& $setup backup` by hand to see the message.
- **A share shows "Access denied" gaps.** The scan account can't read those folders. That's expected for folders you deliberately restricted; otherwise grant Read as in step 3 of [INSTALL-DATASCOPE.md](INSTALL-DATASCOPE.md).

## What has and hasn't been tested

Every change to Data Scope runs this whole flow automatically on a fresh Windows Server: install with PowerShell 5.1, sign in with Windows, scan a local share with an access-denied folder and a junction loop, check that a synthetic SSN file is labelled Restricted with redacted evidence, check that folder permissions were read and who can open that file is computed, check that no file changed, upgrade in place, check that saved passwords are encrypted and the owner settings are admin-only, run the nightly backup task, restore the pre-upgrade backup and check everything again, then uninstall. That machine is not in a domain and uses local accounts. A second run does the same with the signed one-file installer, quietly: install with its own PostgreSQL, sign in and scan, upgrade, uninstall keeping the database, install again onto it, then remove everything.

These parts still need your lab and are **UNVERIFIED** until someone runs them (see the checklist in `docs/WINDOWS-LAB.md`):
- Kerberos sign-in with domain accounts and AD groups.
- Running the scanner as a gMSA.
- Scanning a separate file server over the network, including reading file contents and permissions.
- Directory sync from a real domain controller with Kerberos, and access results compared with Windows' own Effective Access tab.
- Owners with no Data Scope role signing in with Windows, and email through your mail server.
- Opening reports on a domain client and printing them to PDF.
- Encrypted settings on a domain server, restoring on a new server, the nightly backup over several days, and TLS 1.3.
- Last-access times, offline and cloud-only files, and antivirus while contents are read.
- The installer on Windows Server 2019 and 2025 (CI uses the current GitHub Windows Server image).
