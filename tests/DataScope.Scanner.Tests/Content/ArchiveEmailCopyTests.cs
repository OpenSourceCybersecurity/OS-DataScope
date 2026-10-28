using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Tests.Content;

/// <summary>Zips, saved emails, and backup, old and temporary copies. Every file is synthetic.</summary>
public sealed class ArchiveEmailCopyTests : IDisposable
{
    private const string Ssn = "512-48-3307";
    private readonly string dir = Directory.CreateTempSubdirectory("ds-content-").FullName;
    private readonly ContentLimits limits = new();

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private ContentExtraction Read(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return new FileContentReader(limits).Read(path, CancellationToken.None);
    }

    private ContentExtraction Read(string name, string text) => Read(name, Encoding.UTF8.GetBytes(text));

    private static byte[] Csv(string ssn = Ssn) => Encoding.UTF8.GetBytes($"Name,SSN\nAvery Okafor,{ssn}\n");

    // ---------- zip ----------

    [Fact]
    public void Reads_the_files_inside_a_zip_with_the_same_readers()
    {
        var zip = Fixtures.Zip(
            ("export/people.csv", Csv()),
            ("letters/offer.docx", Fixtures.Docx("Routing 021000021 account 4417 1234 5678 9113")),
            ("empty-folder/", []));
        var r = Read("hr-archive.zip", zip);
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.Contains("Routing 021000021", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip_of_ssns_is_found_by_the_detector()
    {
        var rows = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"Person {i},512-48-{3300 + i}"));
        var r = Read("payroll-2019.zip", Fixtures.Zip(("payroll.csv", "Name,SSN\n" + rows)));
        var finding = Assert.Single(TextScanner.Scan(r.Text!).Findings);
        Assert.Equal(("DS-SSN-01", 20), (finding.RuleId, finding.Count));
    }

    [Fact]
    public void Says_how_much_of_a_zip_was_read()
    {
        var r = Read("mixed.zip", Fixtures.Zip(("people.csv", Csv()), ("setup.exe", [0x4D, 0x5A]), ("intro.mp4", [0, 0, 0, 24])));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.Equal("Read 1 of 3 files in the zip: 2 of types not read.", r.Note);
    }

    [Fact]
    public void A_zip_of_types_not_read_is_unsupported_not_clean()
    {
        var r = Read("installers.zip", Fixtures.Zip(("setup.exe", [0x4D, 0x5A]), ("intro.mp4", [0, 0, 0, 24])));
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Null(r.Text);
    }

    [Fact]
    public void An_empty_zip_is_scanned_with_no_text()
    {
        var r = Read("empty.zip", Fixtures.Zip(Array.Empty<(string, byte[])>()));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal("", r.Text);
    }

    [Fact]
    public void Password_protected_entries_are_counted_never_opened()
    {
        var zip = Fixtures.Zip(("secret.csv", Csv()));
        MarkEncrypted(zip);
        var r = Read("locked.zip", zip);
        Assert.Equal(ContentState.Encrypted, r.State);
        Assert.Null(r.Text);

        var mixed = Fixtures.Zip(("secret.csv", Csv("512-48-3308")), ("open.csv", Csv()));
        MarkEncrypted(mixed, firstOnly: true);
        var m = Read("some-locked.zip", mixed);
        Assert.Equal(ContentState.Partial, m.State);
        Assert.Contains("1 password-protected", m.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("512-48-3308", m.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_zips_inside_zips_only_to_the_nesting_limit()
    {
        limits.MaxNestingDepth = 3;
        var level3 = Fixtures.Zip(("deepest.csv", Csv("512-48-3309")));
        var level2 = Fixtures.Zip(("deep.csv", Csv("512-48-3308")), ("level3.zip", level3));
        var level1 = Fixtures.Zip(("inner.csv", Csv()), ("level2.zip", level2));
        var r = Read("outer.zip", Fixtures.Zip(("level1.zip", level1)));
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.Contains("512-48-3308", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("512-48-3309", r.Text, StringComparison.Ordinal);
        Assert.Equal(ContentState.Partial, r.State);
    }

    [Fact]
    public void A_zip_bomb_stops_at_the_decompression_limit()
    {
        limits.MaxUncompressedBytes = 1024 * 1024;
        var entries = Enumerable.Range(0, 5).Select(i => ($"part{i}.txt", new string(' ', 600_000))).ToArray();
        var bytes = Fixtures.Zip(entries);
        Assert.True(bytes.Length < 100_000, "the fixture should be a small file that expands hugely");
        var r = Read("bomb.zip", bytes);
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("stopped at a safety limit", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_that_lies_about_its_size_is_never_read_past_it()
    {
        var zip = Fixtures.Zip(("report.docx", Fixtures.Docx(new string('a', 200_000))));
        LieAboutUncompressedSize(zip, 100);
        var r = Read("liar.zip", zip);
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Contains("1 damaged or unreadable", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_zips_with_too_many_entries_before_reading_them()
    {
        limits.MaxArchiveEntries = 5;
        var r = Read("many.zip", Fixtures.Zip(Enumerable.Range(0, 10).Select(i => ($"f{i}.txt", "x")).ToArray()));
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Contains("safety limit", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Large_zips_are_skipped_without_opening()
    {
        var reader = new FileContentReader(limits);
        Assert.Equal(ContentState.TooLarge, reader.Precheck("huge.zip", limits.MaxArchiveBytes + 1, FileAttributes.Normal)?.State);
        Assert.Equal(ContentState.TooLarge, reader.Precheck("huge.eml", limits.MaxEmailBytes + 1, FileAttributes.Normal)?.State);
        Assert.Null(reader.Precheck("huge.bak", long.MaxValue, FileAttributes.Normal));
    }

    [Theory]
    [InlineData("not a zip at all")]
    [InlineData("PK\u0003\u0004 then nothing that makes sense")]
    public void Damaged_zips_are_unreadable(string content) =>
        Assert.Equal(ContentState.Unreadable, Read("broken.zip", content).State);

    [Fact]
    public void Only_the_start_of_a_large_text_file_in_a_zip_is_read()
    {
        limits.MaxChars = 1_000;
        var r = Read("logs.zip", Fixtures.Zip(("app.log", new string('x', 50_000))));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal(1_000, r.Text!.Length);
    }

    // ---------- email ----------

    private const string Plain = """
        From: "Dana Whitfield" <dana@northwind.test>
        To: payroll@northwind.test
        Subject: March payroll corrections
        Date: Tue, 10 Mar 2026 09:14:00 -0500
        Message-ID: <a1@northwind.test>
        Content-Type: text/plain; charset=utf-8

        Please fix the SSN for Avery Okafor: 512-48-3307.
        """;

    [Fact]
    public void Reads_a_plain_email_subject_and_body_but_not_addresses()
    {
        var r = Read("payroll.eml", Plain.ReplaceLineEndings("\r\n"));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("March payroll corrections", r.Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("dana@northwind.test", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_encoded_bodies_html_and_attachments()
    {
        var csv = Convert.ToBase64String(Csv("512-48-3308"), Base64FormattingOptions.InsertLineBreaks);
        var xlsx = Convert.ToBase64String(Fixtures.Xlsx([["Name", "SSN"], ["Avery", "512-48-3309"]]), Base64FormattingOptions.InsertLineBreaks);
        var html = Convert.ToBase64String(Encoding.UTF8.GetBytes("<html><head><style>p{}</style></head><body><p>Card&#58; 4111 1111 1111 1111</p><script>var x='512-00-0000'</script></body></html>"));
        var logo = Convert.ToBase64String(Fixtures.Image("tiny.png")); // Pictures in the message are read too; this one is too small to hold text.
        var eml = $"""
            From: hr@northwind.test
            Subject: =?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes("Résumé and benefits"))}?=
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="outer"

            --outer
            Content-Type: multipart/alternative; boundary="alt"

            --alt
            Content-Type: text/plain; charset=iso-8859-1
            Content-Transfer-Encoding: quoted-printable

            Caf=E9 meeting. SSN 512-48-=
            3307 on file.
            --alt
            Content-Type: text/html; charset=utf-8
            Content-Transfer-Encoding: base64

            {html}
            --alt--
            --outer
            Content-Type: text/csv; name="people.csv"
            Content-Disposition: attachment; filename="people.csv"
            Content-Transfer-Encoding: base64

            {csv}
            --outer
            Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet
            Content-Disposition: attachment; filename*=utf-8''benefits%20list.xlsx
            Content-Transfer-Encoding: base64

            {xlsx}
            --outer
            Content-Type: image/png; name="logo.png"
            Content-Disposition: inline; filename="logo.png"
            Content-Transfer-Encoding: base64

            {logo}
            --outer--
            """;
        var r = Read("benefits.eml", eml.ReplaceLineEndings("\r\n"));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Résumé and benefits", r.Text, StringComparison.Ordinal);
        Assert.Contains("Café meeting. SSN 512-48-3307 on file.", r.Text, StringComparison.Ordinal);
        Assert.Contains("Card: 4111 1111 1111 1111", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("512-00-0000", r.Text, StringComparison.Ordinal);
        Assert.Contains("512-48-3308", r.Text, StringComparison.Ordinal);
        Assert.Contains("512-48-3309", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_forwarded_emails_and_says_which_attachments_were_not_read()
    {
        var eml = """
            Subject: Fwd: records
            Content-Type: multipart/mixed; boundary=b1

            --b1
            Content-Type: text/plain

            See below.
            --b1
            Content-Type: message/rfc822

            From: someone@northwind.test
            Subject: The records
            Content-Type: text/plain

            SSN 512-48-3307
            --b1
            Content-Type: application/octet-stream
            Content-Disposition: attachment; filename="tool.exe"
            Content-Transfer-Encoding: base64

            TVqQAAMAAAAEAAAA
            --b1--
            """;
        var r = Read("fwd.eml", eml);
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("The records", r.Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.Equal("Read the email and 0 of 1 attachments: 1 of types not read.", r.Note);
    }

    [Fact]
    public void Deeply_nested_email_parts_stop_without_failing()
    {
        var sb = new StringBuilder("Subject: nest\r\n");
        for (var i = 0; i < 40; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Content-Type: multipart/mixed; boundary=b{i}\r\n\r\n--b{i}\r\n");
        }
        sb.Append("Content-Type: text/plain\r\n\r\nSSN 512-48-3307\r\n");
        var r = Read("nested.eml", sb.ToString());
        Assert.Equal(ContentState.Partial, r.State);
        Assert.DoesNotContain(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_count_towards_the_nesting_limit()
    {
        limits.MaxNestingDepth = 1;
        var zip = Convert.ToBase64String(Fixtures.Zip(("people.csv", Csv())));
        var eml = $"Subject: zipped\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: application/zip\r\nContent-Disposition: attachment; filename=people.zip\r\nContent-Transfer-Encoding: base64\r\n\r\n{zip}\r\n--b--\r\n";
        var r = Read("zipped.eml", eml);
        Assert.Equal(ContentState.Partial, r.State);
        Assert.DoesNotContain(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_email_is_read_as_far_as_it_goes()
    {
        var r = Read("broken.eml", "Subject: no body and a bad boundary\r\nContent-Type: multipart/mixed\r\n\r\nSSN 512-48-3307");
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.Equal(ContentState.Unreadable, Read("bad-base64.eml",
            "Content-Type: application/pdf\r\nContent-Disposition: attachment; filename=a.pdf\r\nContent-Transfer-Encoding: base64\r\n\r\n!!!!").State);
    }

    // ---------- backup, old and temporary copies ----------

    [Theory]
    [InlineData("payroll.xlsx.bak")]
    [InlineData("payroll.old")]
    [InlineData("~WRL0001.tmp")]
    public void Reads_copies_of_office_files_by_their_contents(string name)
    {
        var r = Read(name, Fixtures.Xlsx([["Name", "SSN"], ["Avery", Ssn]]));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_copies_of_text_pdf_zip_word_and_email()
    {
        Assert.Contains(Ssn, Read("employees.csv.old", Csv()).Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, Read("statement.bak", Fixtures.Pdf($"SSN {Ssn}")).Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, Read("export.tmp", Fixtures.Zip(("people.csv", Csv()))).Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, Read("letter.orig", Fixtures.Docx($"SSN {Ssn}")).Text, StringComparison.Ordinal);
        var mail = Read("message.bak", Plain);
        Assert.DoesNotContain("dana@northwind.test", mail.Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Binary_copies_such_as_database_backups_are_not_read_as_text()
    {
        var bytes = new byte[64 * 1024];
        new Random(7).NextBytes(bytes);
        var r = Read("hr.bak", bytes);
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Contains("binary format", r.Note, StringComparison.Ordinal);
        // An older Office copy is read as one (LegacyFormatTests); this one is only its first bytes.
        Assert.Equal(ContentState.Unreadable, Read("old-budget.bak", [.. OfficeOpenXml.Ole, 0, 0, 0, 0]).State);
    }

    [Fact]
    public void Copies_have_the_limits_of_what_they_turn_out_to_be()
    {
        limits.MaxOfficeBytes = 1_000;
        var r = Read("big.xlsx.bak", Fixtures.Xlsx([["Name", "SSN"], .. Enumerable.Range(0, 200).Select(i => new string?[] { $"Person {i}", Ssn })]));
        Assert.Equal(ContentState.TooLarge, r.State);
    }

    // ---------- helpers ----------

    private static class OfficeOpenXml
    {
        public static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    }

    /// <summary>Sets the "encrypted" flag on entries in the local and central headers, as a password-protected zip has.</summary>
    private static void MarkEncrypted(byte[] zip, bool firstOnly = false)
    {
        int local = 0, central = 0;
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            var sig = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i));
            if (sig == 0x04034B50 && (!firstOnly || local++ == 0))
            {
                zip[i + 6] |= 1;
            }
            else if (sig == 0x02014B50 && (!firstOnly || central++ == 0))
            {
                zip[i + 8] |= 1;
            }
        }
    }

    private static void LieAboutUncompressedSize(byte[] zip, uint size)
    {
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            var sig = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i));
            if (sig == 0x04034B50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 22), size);
            }
            else if (sig == 0x02014B50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 24), size);
            }
        }
    }
}
