using System.Buffers.Binary;
using System.Text;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;

namespace DataScope.Scanner.Tests.Content;

/// <summary>
/// Older formats (D132): Word, Excel and PowerPoint 97-2003 files, Outlook .msg files, and 7-Zip and RAR archives.
/// Every file is synthetic: saved by LibreOffice or py7zr from made-up data by scripts/make-legacy-fixtures.py, or built
/// here (Outlook messages, damaged files, encrypted files, and RAR archives, which no free tool writes).
/// </summary>
public sealed class LegacyFormatTests : IDisposable
{
    private const string Ssn = "512-48-3307";
    private readonly string dir = Directory.CreateTempSubdirectory("ds-legacy-").FullName;
    private readonly ContentLimits limits = new();

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private ContentExtraction Read(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return new FileContentReader(limits).Read(path, CancellationToken.None);
    }

    private static byte[] Legacy(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Content", "Fixtures", "legacy", name));

    private static List<FindingCategory> Found(ContentExtraction r) =>
        r.Text is null ? [] : TextScanner.Scan(r.Text).Findings.Select(f => f.Category).ToList();

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    // ---------- Word, Excel and PowerPoint 97-2003 ----------

    [Fact]
    public void A_word_97_document_is_read_with_its_tables()
    {
        var r = Read("payroll-change.doc", Legacy("payroll-change.doc"));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Social Security Number: 512-48-3307\n", r.Text, StringComparison.Ordinal);
        Assert.Contains("Avery Example, Zürich office", r.Text, StringComparison.Ordinal); // 8-bit text in Windows-1252.
        Assert.Contains("Name\tSSN\t\nJordan Sample\t512-48-3308\t\n", r.Text, StringComparison.Ordinal); // One line per row.
        Assert.Contains(FindingCategory.UsSsn, Found(r));
    }

    [Fact]
    public void An_excel_97_workbook_is_read_row_by_row_with_numbers_as_stored()
    {
        var r = Read("staff.xls", Legacy("staff.xls"));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Name\tSSN\tCard\tRate\t\nAvery Example\t512483307\t4111111111111111\t12.5\t\n", r.Text, StringComparison.Ordinal);
        Assert.Contains("Jordan Sample\t512-48-3308\t\t7\t", r.Text, StringComparison.Ordinal); // The empty cell keeps its column.
        Assert.Contains("Reviewed by Casey Demo", r.Text, StringComparison.Ordinal); // The second sheet.
        Assert.Contains(FindingCategory.UsSsn, Found(r));
        Assert.Contains(FindingCategory.PaymentCard, Found(r));
    }

    [Fact]
    public void A_powerpoint_97_presentation_is_read_with_its_notes_but_not_the_masters_prompts()
    {
        var r = Read("review.ppt", Legacy("review.ppt"));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Case for Casey Demo\nMember SSN 512-48-3307\n", r.Text, StringComparison.Ordinal);
        Assert.Contains("Card on file 4111 1111 1111 1111", r.Text, StringComparison.Ordinal); // A speaker note.
        Assert.DoesNotContain("Click to edit", r.Text, StringComparison.Ordinal);
        Assert.Contains(FindingCategory.PaymentCard, Found(r));
    }

    [Theory]
    [InlineData("payroll-change.docx")] // An older file given a newer name.
    [InlineData("payroll-change.bak")] // A copy, read by what it holds.
    [InlineData("payroll-change")]
    public void An_older_office_file_is_read_whatever_its_name(string name)
    {
        var r = Read(name, Legacy("payroll-change.doc"));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_older_office_file_inside_a_zip_is_read()
    {
        var r = Read("archive.zip", Fixtures.Zip(("2003/staff.xls", Legacy("staff.xls"))));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("512483307", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_protected_newer_office_files_are_reported_as_encrypted()
    {
        // Office saves a protected .docx, .xlsx or .pptx as a compound file holding the encrypted package.
        var locked = CompoundFileBuilder.Build(new CfbData("EncryptionInfo", new byte[200]), new CfbData("EncryptedPackage", new byte[5000]));
        var r = Read("locked.xlsx", locked);
        Assert.Equal(ContentState.Encrypted, r.State);
        Assert.Equal("The file is password-protected.", r.Note);
    }

    [Fact]
    public void A_password_protected_word_97_document_is_reported_as_encrypted()
    {
        var doc = Legacy("payroll-change.doc");
        var fib = FindWordHeader(doc);
        doc[fib + 0x0B] |= 0x01; // fEncrypted.
        Assert.Equal(ContentState.Encrypted, Read("locked.doc", doc).State);
    }

    [Fact]
    public void Word_95_and_older_documents_are_named_as_not_read()
    {
        var doc = Legacy("payroll-change.doc");
        BinaryPrimitives.WriteUInt16LittleEndian(doc.AsSpan(FindWordHeader(doc) + 2), 0x0065); // nFib of Word 6.
        var r = Read("old.doc", doc);
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Equal("Word documents from Word 95 and earlier are not read.", r.Note);
    }

    /// <summary>The start of the WordDocument stream, at the start of a sector: wIdent 0xA5EC.</summary>
    private static int FindWordHeader(byte[] doc)
    {
        for (var i = 512; i < doc.Length - 4; i += 64)
        {
            if (doc[i] == 0xEC && doc[i + 1] == 0xA5 && BinaryPrimitives.ReadUInt16LittleEndian(doc.AsSpan(i + 2)) >= 0x00C1)
            {
                return i;
            }
        }
        throw new InvalidOperationException("No Word header in the fixture.");
    }

    // ---------- Excel records built here ----------

    private static byte[] Record(ushort type, params byte[] body) =>
        [.. BitConverter.GetBytes(type), .. BitConverter.GetBytes((ushort)body.Length), .. body];

    private static byte[] Bof(ushort kind) => Record(0x0809, [0x00, 0x06, (byte)kind, (byte)(kind >> 8), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    private static byte[] Cell(ushort row, ushort column) => [.. BitConverter.GetBytes(row), .. BitConverter.GetBytes(column), 0x0F, 0x00];

    private static byte[] Workbook(params byte[][] records) =>
        CompoundFileBuilder.Build(new CfbData("Workbook", records.SelectMany(r => r).ToArray()));

    [Fact]
    public void Excel_97_strings_split_across_records_formula_results_and_packed_numbers_are_read()
    {
        // The shared string table holds "Avery Example" and an SSN whose characters continue in a CONTINUE record,
        // switching from 8-bit to 16-bit characters there, as Excel does.
        byte[] sst = [2, 0, 0, 0, 2, 0, 0, 0, 13, 0, 0, .. "Avery Example"u8, 11, 0, 0, .. "512-48"u8];
        byte[] rest = [0x01, .. Utf16("-3307")];
        byte[] formula = [.. Cell(1, 2), 0x00, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] text = [9, 0, 0, .. "Badge 101"u8];
        byte[] mulRk = [.. BitConverter.GetBytes((ushort)2), 0, 0, 0x0F, 0, .. BitConverter.GetBytes((512483307u << 2) | 2), 0x0F, 0, .. BitConverter.GetBytes((1250u << 2) | 3), 1, 0];
        var r = Read("staff.xls", Workbook(
            Bof(0x0005), Record(0x00FC, sst), Record(0x003C, rest), Record(0x000A),
            Bof(0x0010),
            Record(0x00FD, [.. Cell(0, 0), 0, 0, 0, 0]),
            Record(0x00FD, [.. Cell(0, 1), 1, 0, 0, 0]),
            Record(0x0006, formula), Record(0x0207, text),
            Record(0x00BD, mulRk),
            Record(0x000A)));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Avery Example\t512-48-3307\t\n\t\tBadge 101\t\n512483307\t12.5\t\n", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_protected_excel_97_workbook_is_reported_as_encrypted()
    {
        var r = Read("locked.xls", Workbook(Bof(0x0005), Record(0x002F, new byte[54]), Record(0x000A)));
        Assert.Equal(ContentState.Encrypted, r.State);
    }

    [Fact]
    public void Excel_95_and_older_workbooks_are_named_as_not_read()
    {
        var r = Read("old.xls", CompoundFileBuilder.Build(new CfbData("Book", Record(0x0809, new byte[16]))));
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Equal("Excel workbooks from Excel 95 and earlier are not read.", r.Note);
    }

    [Fact]
    public void An_excel_record_running_past_the_end_stops_the_reading_there()
    {
        byte[] cut = [.. Record(0x00FD, [.. Cell(0, 0), 0, 0, 0, 0]), 0xFD, 0x00, 0xFF, 0x7F, 1, 2];
        var r = Read("cut.xls", Workbook(Bof(0x0005), Record(0x00FC, [1, 0, 0, 0, 1, 0, 0, 0, 3, 0, 0, .. "abc"u8]), Bof(0x0010), cut));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("abc", r.Text, StringComparison.Ordinal);
    }

    // ---------- damaged and unusual compound files ----------

    [Fact]
    public void A_damaged_compound_file_is_unreadable()
    {
        var r = Read("old.doc", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal("The file is damaged or not in the format its name suggests.", r.Note);
    }

    [Fact]
    public void A_chain_of_sectors_that_loops_ends_as_unreadable()
    {
        var file = CompoundFileBuilder.Build(new CfbData("WordDocument", new byte[9000]));
        // Point every FAT entry back at the first sector, so following any chain would loop for ever.
        var fatSector = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x4C));
        var fat = (int)(fatSector + 1) * 512;
        for (var i = 0; i < 17; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(fat + (i * 4)), 0);
        }
        var r = Read("loop.doc", file);
        Assert.Equal(ContentState.Unreadable, r.State);
    }

    [Fact]
    public void A_part_larger_than_the_safety_limit_is_not_read()
    {
        limits.MaxUncompressedBytes = 4096;
        var r = Read("big.doc", Legacy("payroll-change.doc"));
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal("A part of the file is larger than the safety limit.", r.Note);
    }

    [Fact]
    public void Other_compound_files_are_named_and_not_read()
    {
        var r = Read("drawing.vsd", CompoundFileBuilder.Build(new CfbData("VisioDocument", new byte[100])));
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.StartsWith("The contents are in a Microsoft compound file format Data Scope does not read", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Older_office_text_stops_at_the_character_limit()
    {
        limits.MaxChars = 40;
        var r = Read("payroll-change.doc", Legacy("payroll-change.doc"));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal(40, r.Text!.Length);
    }

    // ---------- Outlook messages ----------

    private static CfbData Prop(string id, string value) => new($"__substg1.0_{id}001F", [.. Utf16(value), 0, 0]);

    private static CfbStorage Attachment(int n, string name, byte[] data) =>
        new($"__attach_version1.0_#{n:X8}", Prop("3707", name), new CfbData("__substg1.0_37010102", data));

    private static byte[] Msg(params CfbNode[] parts) =>
        CompoundFileBuilder.Build([new CfbData("__properties_version1.0", new byte[32]), .. parts]);

    [Fact]
    public void An_outlook_message_is_read_with_its_attachments_and_without_addresses()
    {
        var csv = Encoding.UTF8.GetBytes($"Name,SSN\nAvery Example,{Ssn}\n");
        var r = Read("new hire.msg", Msg(
            Prop("0037", "New hire paperwork"),
            Prop("1000", "Please find the form attached."),
            Prop("0C1F", "hr-sender@example.com"),
            new CfbStorage("__recip_version1.0_#00000000", Prop("39FE", "jordan.sample@example.com")),
            Attachment(0, "people.csv", csv)));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("New hire paperwork", r.Text, StringComparison.Ordinal);
        Assert.Contains("Please find the form attached.", r.Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.com", r.Text, StringComparison.Ordinal);
        Assert.Contains(FindingCategory.UsSsn, Found(r));
    }

    [Fact]
    public void An_outlook_message_with_only_an_html_body_or_8_bit_text_is_read()
    {
        var r = Read("old.msg", Msg(
            new CfbData("__substg1.0_0037001E", Encoding.Latin1.GetBytes("Résumé attached\0")),
            new CfbData("__substg1.0_10130102", Encoding.UTF8.GetBytes($"<html><body><p>SSN {Ssn}</p><script>x()</script></body></html>"))));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains("Résumé attached", r.Text, StringComparison.Ordinal);
        Assert.Contains($"SSN {Ssn}", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("x()", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_attached_to_an_outlook_message_is_read()
    {
        var forwarded = new CfbStorage("__substg1.0_3701000D", Prop("0037", "Original"), Prop("1000", $"Member SSN {Ssn}"));
        var r = Read("fwd.msg", Msg(Prop("0037", "Fwd: Original"), new CfbStorage("__attach_version1.0_#00000000", forwarded)));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains($"Member SSN {Ssn}", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_not_read_are_counted()
    {
        var r = Read("tools.msg", Msg(Prop("0037", "Installer"), Attachment(0, "setup.exe", [0x4D, 0x5A]), Attachment(1, "notes.txt", "fine"u8.ToArray())));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal("Read the email and 1 of 2 attachments: 1 of types not read.", r.Note);
    }

    [Fact]
    public void Messages_attached_inside_each_other_stop_at_the_nesting_limit()
    {
        CfbNode[] message = [Prop("1000", "innermost")];
        for (var i = 0; i < limits.MaxNestingDepth + 2; i++)
        {
            message = [Prop("1000", $"level {i}"), new CfbStorage("__attach_version1.0_#00000000", new CfbStorage("__substg1.0_3701000D", message))];
        }
        var r = Read("chain.msg", Msg(message));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("stopped at a safety limit", r.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("innermost", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outlook_message_saved_under_another_name_is_read_by_its_contents()
    {
        var r = Read("mail.bak", Msg(Prop("0037", "Copy"), Prop("1000", $"SSN {Ssn}")));
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
    }

    // ---------- 7-Zip and RAR ----------

    [Theory]
    [InlineData("records.7z", "7-Zip archive")]
    [InlineData("records.rar", "RAR archive")]
    public void The_files_in_7zip_and_rar_archives_are_read(string fixture, string what)
    {
        var r = Read(fixture, Legacy(fixture));
        Assert.Equal(ContentState.Partial, r.State); // The program inside is not opened.
        Assert.Equal($"Read 2 of 3 files in the {what}: 1 of types not read.", r.Note);
        Assert.Contains($"SSN {Ssn}", r.Text, StringComparison.Ordinal);
        Assert.Contains(FindingCategory.UsSsn, Found(r));
    }

    [Fact]
    public void Archives_are_read_by_their_contents_and_inside_zips()
    {
        Assert.Contains(Ssn, Read("backup.bin", Legacy("records.7z")).Text, StringComparison.Ordinal);
        Assert.Contains(Ssn, Read("nested.zip", Fixtures.Zip(("old/records.rar", Legacy("records.rar")))).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_7zip_archive_with_its_file_list_encrypted_is_reported_as_encrypted()
    {
        var r = Read("locked.7z", Legacy("locked-names.7z"));
        Assert.Equal(ContentState.Encrypted, r.State);
        Assert.Equal("The 7-Zip archive is password-protected, including its list of files.", r.Note);
    }

    [Fact]
    public void Password_protected_files_in_a_7zip_archive_are_counted_without_the_password()
    {
        var r = Read("locked.7z", Legacy("locked-files.7z"));
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal("No file in the 7-Zip archive could be read: 2 password-protected, 1 of types not read.", r.Note);
    }

    [Fact]
    public void Damaged_archives_are_unreadable()
    {
        var seven = Legacy("records.7z");
        Assert.Equal(ContentState.Unreadable, Read("cut.7z", seven[..40]).State);
        Assert.Equal(ContentState.Unreadable, Read("magic.rar", "Rar!\x1A\x07\x01\x00"u8.ToArray()).State);
    }

    [Fact]
    public void Archives_with_more_files_than_the_limit_are_refused()
    {
        limits.MaxArchiveEntries = 2;
        var r = Read("records.7z", Legacy("records.7z"));
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal("The 7-Zip archive holds more than 2 files, the safety limit.", r.Note);
    }

    [Fact]
    public void Archives_stop_at_the_unpacked_bytes_limit()
    {
        limits.MaxUncompressedBytes = 50;
        var r = Read("records.rar", Rar(("a.txt", Encoding.UTF8.GetBytes(new string('a', 40))), ("b.txt", Encoding.UTF8.GetBytes($"SSN {Ssn}"))));
        Assert.Equal(ContentState.Partial, r.State);
        Assert.DoesNotContain(Ssn, r.Text, StringComparison.Ordinal);
        Assert.Contains("stopped at a safety limit", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rar_entry_larger_than_it_claims_is_read_only_as_far_as_it_claims()
    {
        // The header says 5 bytes unpack from it; 4,000 are there. Nothing past the 5 is ever unpacked.
        var r = Read("bomb.rar", Rar(("big.txt", Encoding.UTF8.GetBytes(new string('x', 4000)), 5)));
        Assert.Equal("xxxxx", r.Text?.Trim());
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
            }
        }
        return ~crc;
    }

    /// <summary>A RAR 5 archive with its files stored, as scripts/make-legacy-fixtures.py writes them.</summary>
    private static byte[] Rar(params (string Name, byte[] Data)[] files) => Rar(files.Select(f => (f.Name, f.Data, (long)f.Data.Length)).ToArray());

    private static byte[] Rar(params (string Name, byte[] Data, long Claimed)[] files)
    {
        static byte[] Vint(long n)
        {
            var bytes = new List<byte>();
            do
            {
                var b = (byte)(n & 0x7F);
                n >>= 7;
                bytes.Add((byte)(b | (n > 0 ? 0x80 : 0)));
            }
            while (n > 0);
            return [.. bytes];
        }
        static byte[] Header(byte[] body)
        {
            byte[] size = Vint(body.Length);
            return [.. BitConverter.GetBytes(Crc32([.. size, .. body])), .. size, .. body];
        }
        var output = new List<byte>("Rar!\x1A\x07\x01\x00"u8.ToArray());
        output.AddRange(Header([.. Vint(1), .. Vint(0), .. Vint(0)]));
        foreach (var (name, data, claimed) in files)
        {
            var encoded = Encoding.UTF8.GetBytes(name);
            byte[] fields = [.. Vint(0x0004), .. Vint(claimed), .. Vint(0x20), .. BitConverter.GetBytes(Crc32(data)),
                .. Vint(0), .. Vint(0), .. Vint(encoded.Length), .. encoded];
            output.AddRange(Header([.. Vint(2), .. Vint(0x0002), .. Vint(data.Length), .. fields]));
            output.AddRange(data);
        }
        output.AddRange(Header([.. Vint(5), .. Vint(0), .. Vint(0)]));
        return [.. output];
    }
}
