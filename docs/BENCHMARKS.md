# Benchmarks

How fast Data Scope scans and answers, measured by the scale benchmark (`tests/DataScope.Api.Tests/ScaleBenchmark.cs`, D95). It builds a synthetic file server on disk, runs the real scanner, content readers, detectors, PostgreSQL and API against it, checks the results are complete and correct, and times each step. Every file, name and number in it is made up.

## Latest results

Run on 2026-10-10 at 100,000 files, on the build machine below, with production scan settings and no throttling:

Synthetic estate: 100,000 files in 2,204 folders across 4 shares (1,031 with a synthetic SSN, 9,897 of a type that is not read); written to disk in 16.7 s.
Machine: 4 logical processors, Ubuntu 24.04.5 LTS, .NET 10.0.12; scanner, API and PostgreSQL on the same machine; files on local disk.

| Step | Time | Rate |
|---|---:|---:|
| Walk (inventory and permissions) | 16.2 s | 6,155 files/s, 136 folders/s |
| Content read and classify | 14.2 s | 6,328 files read/s |
| First full scan (walk and classify) | 30.5 s | 3,280 files/s |
| Rescan, nothing changed | 15.0 s | 6,672 files/s |

Database size after three scans: 89 MB (0.9 KB per file, including history).

| Page or report | Median of 3 | Response size |
|---|---:|---:|
| Dashboard | 177 ms | 4 KB |
| Coverage | 3 ms | 0 KB |
| Sensitive data summary | 80 ms | 0 KB |
| Sensitive files, first page | 52 ms | 28 KB |
| Exposure, first page | 32 ms | 28 KB |
| Report: sensitive data (CSV) | 87 ms | 213 KB |
| Report: risky permissions (CSV) | 83 ms | 142 KB |
| Report: group access (CSV) | 50 ms | 3 KB |
| Report: shares and owners (HTML) | 90 ms | 6 KB |
| Report: scan coverage (CSV) | 68 ms | 2 KB |

What the rows mean:

- **Walk** lists every folder and file and reads every folder's permissions. **Content read and classify** opens each supported file and looks for sensitive data. A first scan does both; later scans walk everything but only read new and changed files.
- **Rescan, nothing changed** is the nightly case. It costs about the same as the walk.
- Pages and reports are timed as an administrator, three times each, after the scans. Each must finish within a budget (10 seconds, or more for very large runs) or the benchmark fails.
- The estate: four shares of team folders with fifty files each; nine in ten short text documents, one in ten a video clip (a type that is not read; since M11b pictures are read for text, so the estate no longer uses them as the unread type, and picture reading is timed separately below) and about one in a hundred a staff list with a synthetic Social Security number. One share is open to everyone, so exposure has real results. The benchmark checks every file was inventoried, every SSN file is Restricted, every video clip is "not read", the exposure count is above zero and no scan left a gap.

## Exposure map and directory size

The exposure map (M9c, D125) works out, for each distinct set of permissions on folders with sensitive files, which accounts in the directory can read it. Its cost grows with the number of user accounts, so the benchmark also builds a synthetic directory (`DATASCOPE_BENCH_USERS`, default 2,000) with Domain Users, ten team groups that each get Modify on their own team folders, and a nested group. Run on 2026-10-11 at 20,000 files, Debug build, same machine as above:

| Directory | Exposure map | Exposure map, one person |
|---|---:|---:|
| 2,000 users | 339 ms | 124 ms |
| 20,000 users | 1,914 ms | 848 ms |

Both stay well inside the page budget, and the benchmark checks every user in the directory is counted. The time is close to linear in users, because each permission set is checked against every account; it does not grow with the number of files beyond the distinct permission sets. A directory of 100,000 users would be expected to take around 10 seconds for the full map; if that matters for a customer, the next step is to evaluate per group instead of per user.

## What this does not tell you

- **SMB is not measured.** These files are on the scanning machine's local disk. Over the network to a Windows file server each folder listing and file read is a round trip, so expect scans to be several times slower; lab check W39 records the real figure.
- **Office and PDF files cost more** than the short text files here. Each is bounded by the content limits in `DETECTION-LIMITATIONS.md` (size, pages, 30 seconds per file at most).
- **A real directory is messier** than the synthetic one: deeper nesting, foreign security principals and SID history all add work. Lab check W62 covers the exposure map against a real domain.
- The machine is a 4-processor Linux build machine with PostgreSQL on the same machine. Production runs on Windows Server.

## Problems it found

The first runs showed four things that got slower as the inventory grew, all fixed in M5c:

| Problem | Effect before the fix | Fix |
|---|---|---|
| The classify phase kept every saved file tracked in memory, and each save checked all of them for changes. | Reading 100,000 files took 98 s and slowed down as it went. | Clear the tracked files after each batch: 14 s. |
| The "next batch of files to read" query relied on table statistics that are stale right after a walk; PostgreSQL then scanned the whole files table for each batch of 100. | About 100 ms per batch at 60,000 files, growing with the inventory. | Take the next 100 files by primary key, then filter. |
| The same stale statistics made the batch query's join scan every folder once per file. | Several ms per batch, growing with the number of folders. | Three lookups by key instead of one join. |
| Exposure (on the overview and its own page) looked up the file server's local-group timestamp once per folder. | Around 9 ms per folder with sensitive files, about 2 s at 20,000 files when database connections aren't pooled. | Once per request. |

## Running it

CI runs it at 50,000 files on every push and prints the tables in the job summary of the ".NET on Linux" job. To run it yourself (PostgreSQL as in `DEVELOPMENT.md`):

```bash
export DATASCOPE_TEST_CONNECTION="Host=localhost;Database=postgres;Username=datascope;Password=..."
DATASCOPE_BENCH_FILES=100000 DATASCOPE_BENCH_OUT=bench.md \
  dotnet test tests/DataScope.Api.Tests -c Release --filter Category=Benchmark
```

`DATASCOPE_BENCH_FILES` defaults to 20,000 and `DATASCOPE_BENCH_USERS` (the synthetic directory) to 2,000. The run needs about 1 KB of disk per file for the database plus the synthetic files themselves, and deletes both afterwards.

## Reader processes (M11a)

Since M11a, files are read in a separate reader process (D127). On the benchmark's default estate of 20,000 files, run on the development container (4 logical processors, Linux, files on local disk), content read and classify ran at 1,700 to 2,200 files a second with reader processes against 2,400 to 2,700 in the worker itself, over several alternating runs; the machine's own speed varies by about a quarter between runs. Measured directly, a warm reader process adds about 65 microseconds per file. On a real file server each file read over the network takes milliseconds, so the share of time spent on the reader process is smaller there. A fresh reader process runs slower until its code has warmed up, so processes are replaced only every 50,000 files.

## Search box (M12a)

On the development container (4 logical processors, Linux) with 2,000,000 files in 100,000 folders, each search query took milliseconds with the trigram indexes (D128): a file name found once (`payroll`) 3 ms, a common word (`invoice`, 250,000 matches) 3 ms, a fragment matching thousands (`e-4`) 11 ms, a name matching nothing 2 ms; folders by their own name 0.5 to 9 ms, and 190 ms for a word that appears in every parent folder's name but no folder's own. Writing inventory with the indexes: the 20,000-file scale benchmark walked at 1,700 to 1,900 files a second both with and without them over alternating runs, so the extra index work was within the run-to-run noise. Building the indexes during the upgrade reads every file and folder name once; on millions of files allow a few minutes.

## Text in images (M11b)

On the development container (4 logical processors, Linux, Tesseract 5.3 with one thread, as the Windows build runs), each file read through the reader, three runs: a screenshot of a form (760 × 170 pixels, enlarged twice over) 0.17 to 0.24 s; a scanned form page at 150 dpi (PNG or JPEG) 0.21 to 0.29 s; a two-page fax 0.21 to 0.28 s; a picture with no text 0.10 s; a one-page scanned PDF 0.21 to 0.25 s; a dense 300 dpi page of 60 lines (8.4 million pixels) 3.4 s; a 12 megapixel photo of 30 lines 2.5 to 2.9 s. Starting the engine in a new reader process takes 0.1 to 0.5 s, once. A 10-page scanned PDF of dense 200 dpi pages read 6 or 7 pages before the 20 second limit and was marked Partial.

With the Linux build's default OpenMP threads, the same form page took 20 seconds instead of 0.3 while other work kept every processor busy: its threads spin while they wait. Data Scope now limits it to one thread; with four busy processes running alongside, the form page took 0.4 to 0.5 s.

What it means: a share of documents is scanned at about the same speed as before, but each image or scanned page adds roughly its recognition time, since files are read one at a time. 10,000 scanned forms add about an hour; 10,000 dense scanned pages about ten. Turn `ReadTextInImages` off on servers where that matters more than the coverage.
