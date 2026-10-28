# Third-party components

Direct dependencies only; transitive packages are pinned in `packages.lock.json` and `web/package-lock.json`. Every package ships `sbom.cdx.json`, a CycloneDX bill of materials listing every component in it with its version, hash, license and the program that loads it (`scripts/sbom.mjs`). The build fails if a shipped component has a copyleft license, except the two LGPL DLLs of the bundled PostgreSQL, allowed as separate replaceable files (D169).

## Shipped in the product

| Component | Version | License |
|---|---|---|
| ASP.NET Core, Microsoft.Extensions.* (Negotiate auth, Windows Services hosting, health checks) | 10.0.13 | MIT |
| Entity Framework Core | 10.0.13 | MIT |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL License |
| EFCore.NamingConventions | 10.0.1 | Apache-2.0 |
| PdfPig (UglyToad.PdfPig), PDF text extraction | 0.1.16 | Apache-2.0 |
| SharpCompress, reading 7-Zip and RAR archives | 0.50.4 | MIT |
| Tesseract OCR (`tesseract55.dll`, Windows x64 build from the TesseractOCR NuGet package 5.5.2), text in images | 5.5.0 | Apache-2.0 |
| Leptonica (`leptonica-1.85.0.dll`, same package), image processing for Tesseract | 1.85.0 | BSD-2-Clause |
| Compiled into that Leptonica build, not used to decode customer files on Windows: libjpeg-turbo 2.0.6, libpng 1.6.37, libtiff 4.3.0, zlib 1.2.11, giflib | as listed | IJG, BSD-3-Clause and Zlib; libpng-2.0; libtiff; Zlib; MIT |
| Tesseract English model `eng.traineddata` (tessdata_fast) | 4.1.0 | Apache-2.0 |
| System.DirectoryServices.Protocols, LDAP for directory sync | 10.0.13 | MIT |
| System.Security.Cryptography.ProtectedData, DPAPI for saved passwords | 10.0.13 | MIT |
| React, React DOM | 19.1.0 | MIT |
| lucide-react | 0.511.0 | ISC |
| IBM Plex Sans, IBM Plex Mono (@fontsource) | 5.2.6 | SIL OFL 1.1 |
| Bricolage Grotesque (@fontsource-variable) | 5.2.8 | SIL OFL 1.1 |
| PostgreSQL, EDB's Windows x64 build (`app\postgresql`, installed by DataScope-Setup as the Data Scope Database; or installed separately), and its client library libpq | 16 (`scripts/windows-dependencies.env`) | PostgreSQL License |
| Libraries in that PostgreSQL build's `bin` folder: OpenSSL 3; ICU; libxml2, libxslt; zlib; LZ4; Zstandard; MIT Kerberos; curl (where present) | as shipped by EDB with that version; each DLL and its hash is in `sbom.cdx.json` | Apache-2.0; Unicode-3.0; MIT; Zlib; BSD-2-Clause; BSD-3-Clause; MIT; curl |
| gettext's libintl (`libintl-*.dll`) and GNU libiconv (`libiconv-*.dll`), in that PostgreSQL build, for translated messages | as shipped by EDB with that version | LGPL-2.1-or-later. Shipped unmodified as separate DLLs, which you may replace with your own builds. Source: https://ftp.gnu.org/pub/gnu/gettext/ and https://ftp.gnu.org/pub/gnu/libiconv/ ; licence text: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html . On request we also provide the exact source of the versions shipped. (D169) |
| Microsoft Visual C++ Redistributable 2015-2022 x64 (`app\vcredist\vc_redist.x64.exe`), installed when missing | latest from Microsoft at build time | Microsoft Visual C++ Redistributable licence (redistributable code under the Visual Studio licence terms) |

## Build and test only

| Component | Version | License |
|---|---|---|
| xUnit, xunit.runner.visualstudio | 2.9.3, 3.1.5 | Apache-2.0 |
| Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.Mvc.Testing | 18.10.1, 10.0.13 | MIT |
| dotnet-ef | 10.0.13 | MIT |
| TypeScript | 5.8.3 | Apache-2.0 |
| Vite, @vitejs/plugin-react, vite-plugin-singlefile | 6.3.5, 4.5.0, 2.2.0 | MIT |
| Tailwind CSS, @tailwindcss/vite | 4.1.8 | MIT |
| Vitest | 3.2.7 | MIT |
| Samba (AD domain controller for directory tests in CI; installed from Ubuntu packages, never shipped) | Ubuntu 24.04 package | GPL-3.0 |
| Tesseract and Leptonica for the Linux tests (`libtesseract5`, Ubuntu packages, never shipped) | Ubuntu 24.04 package | Apache-2.0, BSD-2-Clause |
| Pillow and DejaVu fonts (to make the synthetic test images, `scripts/make-ocr-fixtures.py`) | Ubuntu 24.04 packages | MIT-CMU, Bitstream Vera |
| LibreOffice and py7zr (to make the synthetic older Office files and 7-Zip archives, `scripts/make-legacy-fixtures.py`) | 24.2 (Ubuntu 24.04), 1.1.3 | MPL-2.0, LGPL-2.1-or-later |
