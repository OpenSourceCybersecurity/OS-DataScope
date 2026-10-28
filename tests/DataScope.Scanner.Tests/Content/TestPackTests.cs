using System.Text;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;
using DataScope.Scanner.Content.Outlook;

namespace DataScope.Scanner.Tests.Content;

/// <summary>
/// The detection test pack (D138): every file in tests/test-pack/files is read as a scan reads it and compared with
/// tests/test-pack/expected.csv, the same comparison Test-DataScopeTestPack.ps1 makes against a lab scan. The files
/// are made by scripts/make-test-pack.py, except the Outlook and password-protected files, which only the builders
/// here can make: set DATASCOPE_WRITE_TEST_PACK=1 to write them, otherwise they are checked against the pack.
/// </summary>
public sealed class TestPackTests
{
    private static readonly string Pack = Path.Combine(AppContext.BaseDirectory, "test-pack");
    private static readonly Lazy<IReadOnlyList<TestPackCase>> Expected = new(() => TestPackCase.Load(Path.Combine(Pack, "expected.csv")));

    public static TheoryData<string> Files => [.. Expected.Value.Select(c => c.File)];

    [Theory]
    [MemberData(nameof(Files))]
    public void Each_file_is_reported_as_expected(string file)
    {
        var expected = Expected.Value.Single(c => c.File == file);
        var (state, note, findings) = Scan(Path.Combine(Pack, "files", file.Replace('/', Path.DirectorySeparatorChar)));
        var label = state is ContentState.Scanned or ContentState.Partial ? ClassificationPolicy.Classify(findings) : Sensitivity.Unknown;
        var problems = expected.Compare(state, label, findings);
        Assert.True(problems.Count == 0,
            $"{file} ({expected.Tests}): {string.Join(" ", problems)} Read {state} ({note}); found {string.Join(", ", findings.Select(f => $"{f.Category.Label()} {f.Confidence}"))}.");
    }

    [Fact]
    public void Every_file_in_the_pack_has_an_expected_result_and_every_expected_file_is_there()
    {
        var root = Path.Combine(Pack, "files");
        var present = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Expected.Value.Select(c => c.File).Order(StringComparer.Ordinal), present);
    }

    [Fact]
    public void Expected_results_name_real_labels_states_and_categories()
    {
        Assert.True(Expected.Value.Count >= 90, $"Only {Expected.Value.Count} files in the pack.");
        Assert.Equal(Expected.Value.Count, Expected.Value.Select(c => c.File).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var c in Expected.Value)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Tests), c.File);
            // A file that is not read has no label, and one that is read always has one.
            Assert.Equal(c.Read is ContentState.Scanned or ContentState.Partial, c.Label != Sensitivity.Unknown);
        }
    }

    [Fact]
    public void The_files_built_here_match_the_pack()
    {
        var write = Environment.GetEnvironmentVariable("DATASCOPE_WRITE_TEST_PACK") == "1";
        foreach (var (path, bytes) in Built())
        {
            if (write)
            {
                File.WriteAllBytes(Path.Combine(SourcePack(), "files", path), bytes);
                continue;
            }
            var committed = Path.Combine(Pack, "files", path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(committed), $"{path} is missing: run DATASCOPE_WRITE_TEST_PACK=1 dotnet test --filter TestPackTests.");
            if (bytes.AsSpan().SequenceEqual(File.ReadAllBytes(committed)))
            {
                continue;
            }
            // zlib's output differs between platforms (Windows and Linux compress the .ost's blocks differently), so a
            // file whose bytes differ must still hold exactly what the builders put in it.
            var fresh = Path.Combine(Directory.CreateTempSubdirectory("ds-pack-").FullName, Path.GetFileName(path));
            try
            {
                File.WriteAllBytes(fresh, bytes);
                var reader = new FileContentReader(new ContentLimits());
                var (now, before) = (reader.Read(fresh, CancellationToken.None), reader.Read(committed, CancellationToken.None));
                var at = now.Text is null || before.Text is null ? -1 : now.Text.Zip(before.Text).TakeWhile(p => p.First == p.Second).Count();
                Assert.True(now.State == before.State && now.Note == before.Note && now.Text == before.Text,
                    $"{path} differs from what the builders make now: run DATASCOPE_WRITE_TEST_PACK=1 dotnet test --filter TestPackTests. "
                    + $"Built now: {now.State} ({now.Note}), {now.Text?.Length} characters; in the pack: {before.State} ({before.Note}), "
                    + $"{before.Text?.Length} characters; the same for the first {at}.");
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(fresh)!, recursive: true);
            }
        }
    }

    /// <summary>Reads a file as the scan does: the checks that need no file access, then the reader, then the detectors.</summary>
    private static (ContentState State, string? Note, IReadOnlyList<Finding> Findings) Scan(string path)
    {
        var reader = new FileContentReader(new ContentLimits());
        var info = new FileInfo(path);
        var extraction = reader.Precheck(info.Name, info.Length, info.Attributes) ?? reader.Read(path, CancellationToken.None);
        if (extraction.Text is not { } text)
        {
            return (extraction.State, extraction.Note, []);
        }
        var scan = TextScanner.ScanLong(text);
        var state = scan.Truncated && extraction.State == ContentState.Scanned ? ContentState.Partial : extraction.State;
        return (state, extraction.Note, scan.Findings);
    }

    private static string SourcePack()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DataScope.sln")))
            {
                return Path.Combine(dir.FullName, "tests", "test-pack");
            }
        }
        throw new InvalidOperationException("Run this from inside the repository to write the test pack.");
    }

    // ---------- the files only these builders can make; every name and number is made up ----------

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    private static CfbData Prop(string id, string value) => new($"__substg1.0_{id}001F", [.. Utf16(value), 0, 0]);

    private static IEnumerable<(string Path, byte[] Bytes)> Built()
    {
        const string formats = "3-File-formats/";
        var form = Encoding.UTF8.GetBytes("Name,SSN\r\nAvery Example,512-48-3390\r\n");
        yield return (formats + "message.msg", CompoundFileBuilder.Build(
            new CfbData("__properties_version1.0", new byte[32]),
            Prop("0037", "New hire paperwork"),
            Prop("1000", "Please set up payroll. The card for expenses is 4111 1111 1111 1111."),
            Prop("0C1F", "hr@example.test"),
            new CfbStorage("__attach_version1.0_#00000000", Prop("3707", "new-hire.csv"), new CfbData("__substg1.0_37010102", form))));

        PstBuilder Mailbox(PstBuilder builder)
        {
            builder.Add(new PstMessage { Subject = "Team lunch", Body = "Pizza on Friday in the break room." });
            builder.Add(new PstMessage { Subject = "Payroll change", Body = "Employee SSN 512-48-3391 moves to the March run." });
            builder.Add(new PstMessage
            {
                Subject = "Refund",
                Html = "<html><body><p>Please refund the customer.</p></body></html>",
                Attachments = [PstAttachment.File("card.txt", "Card on file: 5555 5555 5555 4444")],
            });
            builder.Add(new PstMessage
            {
                Subject = "Fwd: Benefits form",
                Body = "See the original below.",
                Attachments = [PstAttachment.Attached(new PstMessage { Subject = "Benefits form", Body = "Member SSN 512-48-3392" })],
            });
            return builder;
        }
        yield return (formats + "mailbox.pst", Mailbox(new PstBuilder()).Build());
        yield return (formats + "offline-mailbox.ost", Mailbox(new PstBuilder(PstFormat.Unicode4K) { Ost = true }).Build());

        // Office saves a protected .docx or .xlsx as a compound file holding the encrypted package.
        var locked = CompoundFileBuilder.Build(new CfbData("EncryptionInfo", new byte[200]), new CfbData("EncryptedPackage", new byte[5000]));
        yield return ("6-Reported-as-not-read/password-protected.docx", locked);
        yield return ("6-Reported-as-not-read/password-protected.xlsx", locked);
    }
}

/// <summary>One row of expected.csv: what Data Scope should report for one file of the test pack.</summary>
internal sealed record TestPackCase(
    string File, Sensitivity Label, ContentState Read, IReadOnlyList<(FindingCategory Category, Confidence Least)> MustFind,
    IReadOnlyList<FindingCategory>? MustNotFind, string Tests)
{
    public static IReadOnlyList<TestPackCase> Load(string path)
    {
        var lines = System.IO.File.ReadAllLines(path);
        Assert.Equal("File,Label,Read,MustFind,MustNotFind,Tests", lines[0]);
        return [.. lines.Skip(1).Where(l => l.Length > 0).Select(Parse)];
    }

    private static TestPackCase Parse(string line)
    {
        var cells = Cells(line);
        Assert.True(cells.Count == 6, $"Expected 6 columns: {line}");
        return new TestPackCase(cells[0], Enum.Parse<Sensitivity>(cells[1]), Enum.Parse<ContentState>(cells[2]),
            [.. Split(cells[3]).Select(Find)],
            cells[4] == "any" ? null : [.. Split(cells[4]).Select(Category)],
            cells[5]);
    }

    private static string[] Split(string cell) => cell.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (FindingCategory, Confidence) Find(string text)
    {
        var at = text.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? (Category(text), Confidence.Medium) : (Category(text[..at]), Enum.Parse<Confidence>(text[(at + 1)..]));
    }

    private static FindingCategory Category(string label)
    {
        Assert.True(FindingCategoryText.TryParseLabel(label, out var category), $"Unknown category {label}.");
        return category;
    }

    /// <summary>CSV cells, with quoted cells as Excel writes them.</summary>
    private static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                cell.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }
        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>What differs between this row and what a scan reported, in plain words; empty when they agree.</summary>
    public List<string> Compare(ContentState read, Sensitivity label, IReadOnlyList<Finding> findings)
    {
        var problems = new List<string>();
        if (read != Read)
        {
            problems.Add($"Expected to be read as {Read}, was {read}.");
        }
        if (label != Label)
        {
            problems.Add($"Expected label {Label}, was {label}.");
        }
        foreach (var (category, least) in MustFind)
        {
            if (!findings.Any(f => f.Category == category && f.Confidence >= least))
            {
                problems.Add($"Expected {category.Label()} at {least} or higher.");
            }
        }
        var unexpected = findings.Where(f => f.Confidence >= Confidence.Medium && (MustNotFind is null || MustNotFind.Contains(f.Category)));
        foreach (var f in unexpected)
        {
            problems.Add($"Did not expect {f.Category.Label()} at {f.Confidence}.");
        }
        return problems;
    }
}
