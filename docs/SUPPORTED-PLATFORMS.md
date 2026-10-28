# Supported platforms and sizing

What Data Scope V1 runs on, what it scans, and how big a server it needs. "Tested" means an automated test runs it on every change; "lab" means it is in the Windows lab plan (`WINDOWS-LAB.md`) and is UNVERIFIED until that lab run is recorded.

## The Data Scope server

| | Supported | How it is checked |
|---|---|---|
| Windows | Windows Server 2019, 2022 and 2025, 64-bit, Desktop Experience or Server Core, joined to an Active Directory domain. | CI installs, upgrades, backs up, restores and uninstalls on the current GitHub Windows Server image (not domain-joined). 2019 and 2025 in a domain: lab W10. |
| PostgreSQL | 16 or 17, on the same server or another one on the network, with its command line tools (`pg_dump`, `pg_restore`) on the Data Scope server, and the standard `pg_trgm` extension available (it ships with PostgreSQL; D128). DataScope-Setup installs PostgreSQL 16 itself as the Data Scope Database (D169); or bring your own (D26, D89). | Tested against 16 (Linux CI, and the bundled one in Windows CI) and 17 (Windows CI). |
| .NET | Nothing to install: the programs are self-contained. | |
| Visual C++ runtime | Microsoft Visual C++ Redistributable (2015-2022, x64), for reading text in images (D129) and for the bundled PostgreSQL. The package carries it and the installer installs it when missing (D169). Without it, and with your own PostgreSQL, everything else works and images are listed as not read. | The installer's preflight checks it; CI scans a scanned form from the installed package. |
| Browser | Current Microsoft Edge and Google Chrome on domain-joined Windows clients, with the site in the Local intranet zone for Windows sign-in. | Lab W9. Firefox and Safari are not tested. |

The installer's `-PreflightOnly` checks all of this before anything is changed (D92). The setup wizard (`DataScope-Setup-<version>.exe`) needs the desktop: on Server Core, run it with `/quiet` or use the script.

## What it scans

| | Supported in V1 |
|---|---|
| File servers | Windows Server 2016 or later sharing folders over SMB, in the same Active Directory domain as the Data Scope server. Several domains or forests are not in V1. |
| Shares | Disk shares. Administrative shares (`C$`, `ADMIN$`) are listed but never scanned. DFS namespaces are scanned through the target server's own shares, not the namespace path. |
| Directory | On-premises Active Directory over LDAP or LDAPS, read-only. |
| File activity | Windows Security log events 4656, 4660, 4663, 4670, 5140, 5145 and 1102 from the same file servers (D97). Read remotely from each file server's Security log by the scanner's account, which needs the server's Event Log Readers group and the Remote Event Log Management firewall rules; no agent (D102). Audit policy and folder auditing (SACLs) are set by the customer's administrator, with `Enable-DataScopeAuditing.ps1` from the package (D103). |
| Not in V1 | NAS appliances (NetApp, Synology and similar), Azure Files, Entra ID-only accounts on file shares, Linux or macOS file servers. Microsoft 365 (Exchange Online, OneDrive, SharePoint, Teams) is not part of Data Scope. |

## Hardware

The installer refuses to install below the minimum and warns below the recommendation.

| | Minimum | Recommended |
|---|---|---|
| Processors | 2 | 4 |
| Memory | 4 GB | 8 GB, or 16 GB with PostgreSQL on the same server above a million files |
| Free disk | 5 GB | 20 GB plus room for backups (see below) |

### Sizing by number of files

Measured on a 4-processor Linux build machine with files on local disk (`BENCHMARKS.md`): a first full scan handles about 3,000 files a second, a rescan with nothing changed about 6,500 a second, and the database grows by about 1 KB per file. Over SMB to a remote file server a scan will be slower; how much slower is lab check W39. The rows below extrapolate from those measurements and are estimates, not tested limits.

| Files across all servers | Processors | Memory | Disk for the database and 14 nightly backups | First full scan (estimate) |
|---|---|---|---|---|
| Up to 1 million | 4 | 8 GB | 30 GB | about 5 minutes on local disk; several times that over SMB |
| 1 to 5 million | 4 to 8 | 16 GB | 100 GB | up to a few hours over SMB |
| Over 5 million | 8 | 32 GB, PostgreSQL on its own server | 300 GB | plan it over a weekend, with throttling (D94) |

Each job stops at 20 million entries by default (`Scanner:MaxEntriesPerJob`) and says so as a coverage gap.

## Network

Nothing goes to the internet: all connections are inside your network, and no outside host is contacted at runtime.

| From | To | Port | Why |
|---|---|---|---|
| Users' browsers | Data Scope server | 443 (or `-Port`) | The web UI, HTTPS only |
| Data Scope server | File servers | 445 | Reading shares over SMB |
| Data Scope server | Domain controllers | 88, 389 (or 636), 53 | Kerberos, LDAP or LDAPS, DNS |
| Data Scope server | PostgreSQL | 5432 | Only when PostgreSQL is on another server |
| Data Scope server | Your mail relay | 587 or 25 | Only if review emails are turned on |

## Accounts

| Account | Needs | Must not be |
|---|---|---|
| Scan account (a gMSA is best) | Read on each share and read and list on its folders (`RX`). Optionally local administrator on file servers, only to read share permissions and local groups (otherwise those show as Partial). | A domain admin, or a member of any admin group in the domain. |
| Data Scope admin group | Membership is what makes someone a Data Scope admin. | |
| PostgreSQL admin | Used once by the installer to create the database and the two Data Scope logins; not stored. | |
