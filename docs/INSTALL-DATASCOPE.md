# Install DataScope

Install Data Scope on a Windows server in your domain and scan your first file server. It takes about 30 minutes. Every step is read-only for your files: Data Scope never changes files, permissions or Active Directory.

**Use test data in a lab.** Put made-up files on lab shares, never copies of real company files.

In the commands below, replace `YOURDOMAIN` with your domain's short name and `yourdomain.local` with its DNS name.

## What you need

| Machine | Example | Notes |
|---|---|---|
| Domain controller | `dc01` | Any Active Directory domain. |
| File server | `fs01` | Windows Server 2019 or later with a few shares. |
| Data Scope server | `ds01` | Windows Server 2019, 2022 or 2025, joined to the domain. 4 CPUs, 8 GB RAM, 60 GB disk. |
| A PC | | Joined to the domain, to open the web page. |

## 1. Download

1. On GitHub, open **OpenSourceCybersecurity/OS-DataScope**, then **Releases**, and open the latest release.
2. Download `DataScope-Setup-<version>.exe`, its `.sha256` file, `Prepare-DataScopeDomain.ps1` and `Enable-DataScopeAuditing.ps1`.
3. Copy them to `C:\Install` on ds01.

For the newest build between releases, open **Actions**, the latest green **CI** run, and download **DataScope-Setup** under **Artifacts**.

## 2. Create the accounts (on dc01)

Copy `Prepare-DataScopeDomain.ps1` to dc01 and run it in PowerShell as a domain admin. Replace `yourname` with your own account:

```powershell
cd C:\Install
Unblock-File .\Prepare-DataScopeDomain.ps1
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Prepare-DataScopeDomain.ps1 -DataScopeServer ds01 -AdminMembers yourname -Lab
```

It creates the `DataScope-Admins` group with you in it, and the scan account `svc-datascope$` (a group managed service account, so there is no password to manage). It checks every name before changing anything, and `-WhatIf` shows what it would do. `-Lab` makes the account usable straight away; leave it out in production. Then **sign out of Windows and back in** so your sign-in picks up the new group.

## 3. Let the scan account read the shares (on fs01)

In PowerShell as administrator, for each share you want scanned (here `HR`):

```powershell
Grant-SmbShareAccess -Name 'HR' -AccountName 'YOURDOMAIN\svc-datascope$' -AccessRight Read -Force
icacls 'C:\Shares\HR' /grant 'YOURDOMAIN\svc-datascope$:(OI)(CI)RX'
```

Read is all it needs.

## 4. Run the setup wizard (on ds01)

1. Check the download: `(Get-FileHash C:\Install\DataScope-Setup-*.exe -Algorithm SHA256).Hash` should match the `.sha256` file.
2. Double-click `DataScope-Setup-<version>.exe`. Lab builds are not code-signed, so if Windows says **Windows protected your PC**, choose **More info**, **Run anyway**.
3. Keep the suggested answers: the scan account and admin group from step 2, **Install PostgreSQL 16 with Data Scope**, and a self-signed certificate on port 443.
4. Press **Install**. If a check fails, nothing has changed yet; setup says what to fix.
5. On the last page, press **Show the backup key** and save it in your password manager. Backups can't be restored on another server without it.

## 5. Open Data Scope (on the PC)

1. **Trust the certificate.** Copy `C:\ProgramData\DataScope\datascope-https.cer` from ds01 to the PC, double-click it, choose **Install Certificate**, **Local Machine**, **Trusted Root Certification Authorities**.
2. **Add the site to Local intranet** so Windows signs you in: **Internet Options**, **Security**, **Local intranet**, **Sites**, **Advanced**, add `https://ds01.yourdomain.local`.
3. Open **https://ds01.yourdomain.local** in Edge or Chrome and choose **Sign in with Windows**.

"You don't have access yet" means you are not in `DataScope-Admins`, or you haven't signed out and in since step 2.

## 6. Scan

1. **Servers & scans**, **Add server**, enter `fs01`, then **Quick scan**.
2. When it finishes, look at **Sensitive data** (possible SSNs, card numbers, health data and more, with redacted evidence), **Who has access** (who can open each folder and why), and **Risks**.
3. Name folder owners under **Owners**, then start an **Access review**. Owners see it under **My reviews** and record keep or remove decisions; people make the actual changes.
4. **Reports** has CSV and printable reports for auditors.

Servers are scanned again every night at 1 AM.

## Next

- [ADMIN-GUIDE.md](ADMIN-GUIDE.md) (also in the package): email, file activity and alerts, scan schedules and limits, upgrades, backups, uninstalling, troubleshooting, and installing with PowerShell instead of the wizard.
- [WINDOWS-LAB.md](WINDOWS-LAB.md): a script that builds test shares, and the checklist of what still needs testing on a real domain.

**To upgrade**, run a newer `DataScope-Setup-<version>.exe`. It backs up the database first and keeps your settings. **To uninstall**, use **Settings, Apps, Data Scope, Uninstall**; the database and backups are kept.
