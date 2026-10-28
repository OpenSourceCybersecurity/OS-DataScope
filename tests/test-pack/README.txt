DATA SCOPE DETECTION TEST PACK
==============================

What this is
------------
About 100 files with known contents, and expected.csv, which says what Data Scope
should report for each one: its label, whether it was read, and what kinds of data
it must (or must not) find. Put the files on a lab file server, scan it, and
Test-DataScopeTestPack.ps1 checks every file for you.

Every name, number and address in these files is made up. SSNs are in the
512-48-33xx test range, card numbers are the card companies' published test
numbers, and IBANs are the published examples. Nothing here is real customer data.
Use the pack in a lab, not on a customer's server.

What is in it
-------------
files\1-Each-kind-of-data      One plain text file per kind of data Data Scope looks for
                               (SSN, ITIN, cards, bank accounts, IBANs, passwords and keys,
                               health, dates of birth, licences, passports, names with addresses).
files\2-Look-alikes            Numbers and words that look sensitive but are not. These must
                               NOT be labelled Confidential or Restricted (one known false
                               positive is listed as such).
files\3-File-formats           The same kind of value in every format Data Scope reads: text,
                               Word, Excel, PowerPoint (old and new), PDF, OpenDocument, RTF,
                               email, Outlook .msg/.pst/.ost, zip, 7-Zip and RAR.
files\4-Scans-and-pictures     Scanned forms, photos, faxes and screenshots read by text
                               recognition (OCR), including sideways and upside-down scans.
files\5-Wrong-or-no-extension  Files renamed or with no extension: read by what they contain.
files\6-Reported-as-not-read   Password-protected, damaged and oversized files, programs and
                               media. Each must be reported as not read, with a reason, and never
                               counted as clean.
7-Lab-only (made on the server by the install script)
                               A file with the Offline attribute, a folder the scan account is
                               refused, a junction loop, and a path over 260 characters.

How to run it
-------------
1. Copy this zip to the file server (for example fs01) and unzip it, for example to
   C:\Install\DataScope-Test-Pack. Then, in PowerShell as administrator:

     cd C:\Install\DataScope-Test-Pack
     Get-ChildItem -Recurse | Unblock-File

2. Share the pack to Data Scope's scan account, read-only:

     .\Install-DataScopeTestPack.ps1 -ScanAccount 'YOURDOMAIN\svc-datascope$'

   Keep the single quotes and the $ at the end: a gMSA's name ends with $.
   This copies the files to C:\Shares\TestPack and shares it as \\fs01\TestPack.
   Use -Path and -ShareName to choose other names, and -Force to put a fresh copy back.

3. Scan the server and check the results. Run this on the Data Scope server (or any
   computer that can open Data Scope), as an account with a Data Scope role:

     .\Test-DataScopeTestPack.ps1 -Url https://ds01.yourdomain.local -Server fs01 -StartScan

   -StartScan queues a scan of fs01 and waits for it. Leave it out if you already
   scanned. Add -SkipCertificateCheck if this computer does not trust Data Scope's
   certificate yet. Copy expected.csv along with the script if you run it elsewhere.

4. Read the result. Each file gets a line: "ok" or "FAIL" with what was different. The
   last lines say how many checks passed. Every result is also saved to
   test-pack-results.csv next to the script. That file holds only these synthetic
   file names and Data Scope's masked results, so it is safe to share for a fix.

What a FAIL means
-----------------
The file was read, labelled or matched differently from expected.csv. The line says
what was expected and what Data Scope reported. The Tests column of expected.csv says
what each file is testing. "Known gap" in that column marks something Data Scope does
not detect yet, written down so it is not a surprise.

Cleaning up
-----------
     Remove-SmbShare -Name TestPack -Force
     cmd /c rmdir /s /q \\?\C:\Shares\TestPack

Data Scope marks the files as removed on its next scan of the server.
