# Data Scope

**Free, open source, on-premises data governance for Windows file shares and Active Directory.**

Data Scope finds sensitive data on your file shares, shows who can open it and why, and runs access reviews with the people who own the data. It runs entirely inside your network: no cloud, no telemetry, no outside service. It only reads. It never changes files, permissions or Active Directory.

Project home: [opensourcecybersecurity.com](https://opensourcecybersecurity.com)

## What it does

- **Find sensitive data.** Scans Windows SMB shares and reads files by their contents: Office (new and 97-2003), PDF, email, Outlook .msg/.pst/.ost, zip, 7-Zip and RAR, and text in images and scanned pages with local OCR. Detects possible SSNs and ITINs, payment cards, bank numbers and IBANs, credentials and private keys, health information, dates of birth, driver's licence and passport numbers, plus your own rules. Every result is a possible match with a confidence level and masked evidence, never a claim of certainty.
- **Explain who has access.** Reads share and NTFS permissions and Active Directory groups (nested, with loops and unresolved accounts flagged), and explains each person's access path. When the answer is incomplete it says so.
- **Show exposure and risk.** Exposure map, risk list with triage, compliance standards (for example HIPAA, PCI DSS, state breach laws) with an evidence report, and stale and duplicate data.
- **Run access reviews.** Assign data owners, start review campaigns from frozen snapshots, email owners through your own SMTP relay, and record keep, remove, investigate or exception decisions as signed evidence for auditors.
- **Watch file activity.** Reads the Windows Security log for deletes, permission changes and refused attempts, raises alerts, and learns each person's usual activity to flag unusual behaviour.
- **Answer people requests.** Find a person brings together what Data Scope holds about someone for a subject access request.
- **Prove it.** A hash-chained, append-only audit log, progress over time, a System health page, and CSV and printable reports.

## Built with

C# and .NET 10, ASP.NET Core, EF Core and PostgreSQL 16, a Windows worker service, and React with TypeScript. Readers run in separate, memory- and time-capped processes so a malicious or broken file can't take down a scan. See [docs/02-ARCHITECTURE.md](docs/02-ARCHITECTURE.md).

## Get started

- **Install DataScope:** [docs/INSTALL-DATASCOPE.md](docs/INSTALL-DATASCOPE.md) installs Data Scope on a Windows Server in your domain with the one-file setup wizard.
- **Build it yourself:** [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) builds, runs and tests everything locally.
- **Just look at the UI:** `cd web && npm ci && npm run dev` shows a demo on made-up data.

## Documentation

| Topic | Where |
|---|---|
| Architecture | [docs/02-ARCHITECTURE.md](docs/02-ARCHITECTURE.md) |
| Decisions and why | [docs/DECISIONS.md](docs/DECISIONS.md) |
| Threat model | [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md) |
| Supported platforms | [docs/SUPPORTED-PLATFORMS.md](docs/SUPPORTED-PLATFORMS.md) |
| What detection can and can't show | [docs/DETECTION-LIMITATIONS.md](docs/DETECTION-LIMITATIONS.md) |
| Acceptance status | [docs/V1-ACCEPTANCE.md](docs/V1-ACCEPTANCE.md) |
| Windows lab checks | [docs/WINDOWS-LAB.md](docs/WINDOWS-LAB.md) |
| Roadmap | [docs/ROADMAP.md](docs/ROADMAP.md) |

## Status

The core product is built and tested in CI on Linux and Windows. Some checks need a real Active Directory domain and file servers and are marked UNVERIFIED until they are run in a lab; see [docs/V1-ACCEPTANCE.md](docs/V1-ACCEPTANCE.md). Test it in a lab before you rely on it.

## Contributing and security

Contributions are welcome: read [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md). Never put real personal data in issues, tests or pull requests.

## Licence

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
