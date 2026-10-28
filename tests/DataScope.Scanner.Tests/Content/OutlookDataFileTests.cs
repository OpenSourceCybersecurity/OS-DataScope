using System.Buffers.Binary;
using System.Text;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;
using DataScope.Scanner.Content.Outlook;

namespace DataScope.Scanner.Tests.Content;

/// <summary>
/// Outlook data files (.pst and .ost, D134). Every mailbox is built by <see cref="PstBuilder"/> from synthetic messages;
/// no real mailbox is used.
/// </summary>
public sealed class OutlookDataFileTests : IDisposable
{
    private const string Ssn = "512-48-3307";
    private readonly string dir = Directory.CreateTempSubdirectory("ds-pst-").FullName;
    private ContentLimits limits = new();

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private ContentExtraction Read(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return new FileContentReader(limits).Read(path, CancellationToken.None);
    }

    private static PstMessage Payroll(string subject = "Payroll", bool ansi = false) =>
        new() { Subject = subject, Body = $"Employee SSN {Ssn} for the March run.", Ansi = ansi };

    private static PstBuilder Mailbox(params PstMessage[] messages) => Mailbox(new PstBuilder(), messages);

    private static PstBuilder Mailbox(PstBuilder builder, params PstMessage[] messages)
    {
        foreach (var m in messages)
        {
            builder.Add(m);
        }
        return builder;
    }

    // ---------- layouts and encodings ----------

    [Theory]
    [InlineData("Unicode", 0)]
    [InlineData("Unicode", 1)]
    [InlineData("Unicode", 2)]
    [InlineData("Ansi", 0)]
    [InlineData("Ansi", 1)]
    [InlineData("Ansi", 2)]
    [InlineData("Unicode4K", 0)]
    [InlineData("Unicode4K", 1)]
    public void Reads_every_layout_and_encoding(string layout, byte crypt)
    {
        var format = Enum.Parse<PstFormat>(layout);
        var builder = new PstBuilder(format, crypt) { Ost = format == PstFormat.Unicode4K };
        var r = Read(format == PstFormat.Unicode4K ? "user.ost" : "archive.pst", Mailbox(builder, Payroll(ansi: format == PstFormat.Ansi)).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal("Read all 1 message in the mailbox.", r.Note);
        Assert.Contains("Payroll", r.Text, StringComparison.Ordinal);
        Assert.Contains($"SSN {Ssn}", r.Text, StringComparison.Ordinal);
        var finding = Assert.Single(TextScanner.ScanLong(r.Text!).Findings);
        Assert.Equal(FindingCategory.UsSsn, finding.Category);
    }

    [Theory]
    [InlineData("Unicode", false)]
    [InlineData("Unicode", true)]
    [InlineData("Ansi", false)]
    [InlineData("Ansi", true)]
    [InlineData("Unicode4K", false)]
    [InlineData("Unicode4K", true)]
    public void Long_bodies_spread_over_many_blocks_are_read_whole(string layout, bool twoLevels)
    {
        var format = Enum.Parse<PstFormat>(layout);
        // About 60,000 characters: several blocks, listed by an XBLOCK, or by an XXBLOCK of XBLOCKs.
        var body = string.Concat(Enumerable.Repeat("Quarterly figures follow. ", 2_300)) + $"Closing note: SSN {Ssn}.";
        var builder = new PstBuilder(format) { TwoLevelDataTrees = twoLevels };
        var r = Read("long.pst", Mailbox(builder, new PstMessage { Subject = "Long", Body = body }).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.EndsWith($"Closing note: SSN {Ssn}.", r.Text, StringComparison.Ordinal);
        Assert.Contains(body, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Outlook_2013_offline_mailboxes_are_decompressed()
    {
        var builder = new PstBuilder(PstFormat.Unicode4K, crypt: 0) { Ost = true, Compress = true };
        var repeated = string.Concat(Enumerable.Repeat("Same line again and again. ", 500)) + Ssn;
        var r = Read("user@example.test.ost", Mailbox(builder, new PstMessage { Subject = "Compressed", Body = repeated }).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(repeated, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Eight_bit_text_is_read_as_windows_1252()
    {
        var r = Read("old.pst", Mailbox(new PstBuilder(PstFormat.Ansi), new PstMessage { Subject = "Café", Body = $"Müller, SSN {Ssn}", Ansi = true }).Build());
        Assert.Contains("Café", r.Text, StringComparison.Ordinal);
        Assert.Contains($"Müller, SSN {Ssn}", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_subject_prefix_marker_is_left_out()
    {
        // Outlook may store "RE: Payroll" as U+0001, the prefix's length, then the subject.
        var r = Read("mail.pst", Mailbox(new PstMessage { Subject = "\u0001\u0004RE: Payroll", Body = "See below." }).Build());
        Assert.StartsWith("RE: Payroll", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0001', r.Text!);
    }

    // ---------- bodies ----------

    [Fact]
    public void A_message_with_only_an_html_body_is_read_as_text()
    {
        var html = $"<html><head><style>p {{ color: red }}</style></head><body><p>Employee SSN <b>{Ssn}</b></p><script>var x = 1;</script></body></html>";
        var r = Read("mail.pst", Mailbox(new PstMessage { Subject = "Html", Html = html }).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("color", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("var x", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_with_only_a_rich_text_body_is_read_as_text()
    {
        var rtf = RtfTextTests.Uncompressed($@"{{\rtf1\ansi{{\fonttbl{{\f0 Arial;}}}}\pard Employee SSN {Ssn}\par Caf\'e9\par}}");
        var r = Read("mail.pst", Mailbox(new PstMessage { Subject = "Rich", Rtf = rtf }).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains($"Employee SSN {Ssn}\nCafé", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Arial", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plain_body_wins_over_the_others()
    {
        var r = Read("mail.pst", Mailbox(new PstMessage { Subject = "Both", Body = "Plain body", Html = "<p>Html body</p>" }).Build());
        Assert.Contains("Plain body", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Html body", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Contacts_and_appointments_are_read_by_their_subject_and_notes()
    {
        var r = Read("mail.pst", Mailbox(
            new PstMessage { Class = "IPM.Contact", Subject = "Avery Okafor", Body = $"SSN {Ssn} on file" },
            new PstMessage { Class = "IPM.Appointment", Subject = "Benefits review", Body = "Room 4" }).Build());
        Assert.Equal("Read all 2 messages in the mailbox.", r.Note);
        Assert.Contains("Avery Okafor", r.Text, StringComparison.Ordinal);
        Assert.Contains("Benefits review", r.Text, StringComparison.Ordinal);
    }

    // ---------- attachments ----------

    [Fact]
    public void Attachments_and_attached_messages_are_read_with_the_same_readers()
    {
        var message = new PstMessage
        {
            Subject = "Files",
            Body = "Attached.",
            Attachments =
            [
                PstAttachment.File("staff.csv", $"Name,SSN\nAvery Okafor,{Ssn}\n"),
                new PstAttachment("offer.docx", Fixtures.Docx("Routing 021000021 account 4417 1234 5678 9113")),
                PstAttachment.Attached(new PstMessage
                {
                    Subject = "Forwarded",
                    Body = "Inner body",
                    Attachments = [PstAttachment.File("inner.txt", "DOB: 04/12/1980")],
                }),
            ],
        };
        var r = Read("mail.pst", Mailbox(message).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal("Read all 1 message and 3 of 3 attachments in the mailbox.", r.Note);
        Assert.Contains($"Avery Okafor,{Ssn}", r.Text, StringComparison.Ordinal);
        Assert.Contains("Routing 021000021", r.Text, StringComparison.Ordinal);
        Assert.Contains("Forwarded", r.Text, StringComparison.Ordinal);
        Assert.Contains("DOB: 04/12/1980", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_attachments_under_a_two_level_subnode_tree_are_read()
    {
        var builder = new PstBuilder { TwoLevelSubnodeTrees = true };
        var message = new PstMessage
        {
            Subject = "Many",
            Attachments = [.. Enumerable.Range(0, 5).Select(i => PstAttachment.File($"part{i}.txt", $"Part {i} SSN 512-48-330{i}"))],
        };
        var r = Read("mail.pst", Mailbox(builder, message).Build());
        Assert.Equal("Read all 1 message and 5 of 5 attachments in the mailbox.", r.Note);
        Assert.Contains("Part 4 SSN 512-48-3304", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_to_files_and_programs_are_never_opened()
    {
        var message = new PstMessage
        {
            Subject = "Links",
            Body = "See the share.",
            Attachments =
            [
                new PstAttachment(@"\\files\hr\payroll.csv", Encoding.UTF8.GetBytes($"Name,SSN\nA,{Ssn}\n"), Method: 2),
                new PstAttachment("setup.exe", [0x4D, 0x5A, 0x90, 0x00]),
                PstAttachment.File("notes.txt", "Nothing here"),
            ],
        };
        var r = Read("mail.pst", Mailbox(message).Build());
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal("Read all 1 message and 1 of 3 attachments in the mailbox: 2 of types not read.", r.Note);
        Assert.DoesNotContain(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_attachment_over_its_size_limit_is_counted_not_read()
    {
        limits = new ContentLimits { MaxOfficeBytes = 1024 };
        var big = Fixtures.Docx(string.Concat(Enumerable.Range(0, 400).Select(i => $"Row {i} {Guid.NewGuid()} ")));
        Assert.True(big.Length > 1024);
        var message = new PstMessage { Subject = "Big", Body = "Attached.", Attachments = [new PstAttachment("report.docx", big)] };
        var r = Read("mail.pst", Mailbox(message).Build());
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal("Read all 1 message and 0 of 1 attachments in the mailbox: 1 over the size limit.", r.Note);
    }

    [Fact]
    public void Attached_messages_are_opened_only_as_deep_as_allowed()
    {
        var innermost = new PstMessage { Subject = "Level 3", Body = "deepest" };
        var level2 = new PstMessage { Subject = "Level 2", Attachments = [PstAttachment.Attached(innermost)] };
        var level1 = new PstMessage { Subject = "Level 1", Attachments = [PstAttachment.Attached(level2)] };
        var top = new PstMessage { Subject = "Top", Attachments = [PstAttachment.Attached(level1)] };
        limits = new ContentLimits { MaxNestingDepth = 3 };
        var r = Read("mail.pst", Mailbox(top).Build());
        Assert.Contains("Level 2", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Level 3", r.Text, StringComparison.Ordinal);
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("1 of types not read", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mailbox_attached_to_a_message_is_not_opened()
    {
        var inner = Mailbox(Payroll()).Build();
        var message = new PstMessage { Subject = "Archive", Body = "My old mail.", Attachments = [new PstAttachment("old.pst", inner)] };
        var r = Read("mail.pst", Mailbox(message).Build());
        Assert.Equal(ContentState.Partial, r.State);
        Assert.DoesNotContain(Ssn, r.Text, StringComparison.Ordinal);
    }

    // ---------- what is not a message ----------

    [Fact]
    public void Folders_and_other_nodes_are_passed_over()
    {
        var builder = new PstBuilder().AddOther(0x02).AddOther(0x0D).AddOther(0x12);
        var r = Read("mail.pst", Mailbox(builder, Payroll()).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal("Read all 1 message in the mailbox.", r.Note);
    }

    [Fact]
    public void An_empty_mailbox_is_scanned_with_nothing_found()
    {
        var r = Read("empty.pst", new PstBuilder().AddOther(0x02).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Null(r.Text);
        Assert.Equal("The mailbox holds no messages.", r.Note);
    }

    [Fact]
    public void Large_mailboxes_with_trees_of_several_levels_are_read_whole()
    {
        // 600 messages: the node and block trees each need three levels of pages.
        var messages = Enumerable.Range(0, 600).Select(i => new PstMessage { Subject = $"Message {i}", Body = $"Body {i}" }).ToArray();
        var r = Read("big.pst", Mailbox(messages).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Equal("Read all 600 messages in the mailbox.", r.Note);
        Assert.Contains("Message 599", r.Text, StringComparison.Ordinal);
    }

    // ---------- by contents ----------

    [Theory]
    [InlineData("mail.pst.bak")]
    [InlineData("export")]
    [InlineData("archive.dat")]
    public void A_mailbox_is_recognised_by_its_contents(string name)
    {
        var r = Read(name, Mailbox(Payroll()).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.Contains(Ssn, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mailbox_inside_a_zip_is_not_opened()
    {
        var r = Read("backup.zip", Fixtures.Zip(("mail.pst", Mailbox(Payroll()).Build()), ("copy.pst.bak", Mailbox(Payroll()).Build())));
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Equal("The zip holds no file types Data Scope reads.", r.Note);
    }

    // ---------- forms Data Scope does not read ----------

    [Fact]
    public void An_unknown_version_is_named_not_guessed()
    {
        var bytes = Mailbox(Payroll()).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 40);
        var r = Read("future.pst", bytes);
        Assert.Equal(ContentState.Unsupported, r.State);
        Assert.Equal("The file is in a version of the Outlook data file format Data Scope does not know.", r.Note);
    }

    [Fact]
    public void A_file_encrypted_by_windows_information_protection_is_reported_encrypted()
    {
        var bytes = Mailbox(Payroll()).Build();
        bytes[513] = 0x10;
        var r = Read("protected.pst", bytes);
        Assert.Equal(ContentState.Encrypted, r.State);
        Assert.Null(r.Text);
    }

    [Fact]
    public void An_unknown_encoding_is_named_not_guessed()
    {
        var bytes = Mailbox(Payroll()).Build();
        bytes[513] = 0x07;
        Assert.Equal(ContentState.Unsupported, Read("odd.pst", bytes).State);
    }

    // ---------- damage ----------

    [Fact]
    public void A_file_that_only_starts_like_a_mailbox_is_unreadable()
    {
        var bytes = new byte[8192];
        new Random(5).NextBytes(bytes);
        "!BDN"u8.CopyTo(bytes);
        var r = Read("fake.pst", bytes);
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Null(r.Text);
    }

    [Fact]
    public void A_header_pointing_past_any_possible_file_is_damaged()
    {
        var bytes = Mailbox(Payroll()).Build();
        bytes[231] = 0xFF; // The node tree's root, past the largest offset there can be.
        var r = Read("mail.pst", bytes);
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal("The file is damaged or not in the format its name suggests.", r.Note);
    }

    [Fact]
    public void A_damaged_message_is_counted_and_the_rest_are_read()
    {
        var builder = Mailbox(Payroll("First"), Payroll("Second"), Payroll("Third"));
        var bytes = builder.Build();
        bytes[builder.Offsets[builder.MessageBlocks[1]] + 20] ^= 0xFF; // The checksum no longer matches.
        var r = Read("mail.pst", bytes);
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Equal("Read 2 of 3 messages in the mailbox: 1 message damaged.", r.Note);
        Assert.Contains("First", r.Text, StringComparison.Ordinal);
        Assert.Contains("Third", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Second", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mailbox_whose_every_message_is_damaged_is_unreadable()
    {
        var builder = Mailbox(Payroll("First"), Payroll("Second"));
        var bytes = builder.Build();
        foreach (var bid in builder.MessageBlocks)
        {
            bytes[builder.Offsets[bid] + 20] ^= 0xFF;
        }
        var r = Read("mail.pst", bytes);
        Assert.Equal(ContentState.Unreadable, r.State);
        Assert.Equal("None of the 2 messages in the mailbox could be read: the file is damaged.", r.Note);
    }

    [Fact]
    public void A_node_tree_that_loops_back_keeps_what_was_read_before_it()
    {
        var builder = Mailbox(Enumerable.Range(0, 40).Select(i => Payroll($"Message {i}")).ToArray());
        var bytes = builder.Build();
        // The root's last entry points back at the root itself, with a valid checksum, as a crafted file could.
        var root = builder.NodeRootOffset;
        var count = bytes[root + 488];
        Assert.True(bytes[root + 491] > 0 && count > 1, "The node tree should have more than one level.");
        var entry = root + ((count - 1) * 24);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((int)entry + 8), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)root + 504)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((int)entry + 16), (ulong)root);
        RewriteChecksum(bytes, root);
        var r = Read("loop.pst", bytes);
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Contains("or more messages in the mailbox: the rest of the file is damaged", r.Note, StringComparison.Ordinal);
        Assert.Contains("Message 0", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_tree_that_lies_about_its_depth_is_damaged()
    {
        var builder = Mailbox(Payroll());
        var bytes = builder.Build();
        var root = builder.NodeRootOffset;
        bytes[root + 491] = 9; // Deeper than any real file.
        RewriteChecksum(bytes, root);
        var r = Read("deep.pst", bytes);
        Assert.Equal(ContentState.Unreadable, r.State);
    }

    [Fact]
    public void A_node_tree_page_with_a_bad_checksum_is_damaged()
    {
        var builder = Mailbox(Payroll());
        var bytes = builder.Build();
        bytes[builder.NodeRootOffset + 3] ^= 0x40;
        Assert.Equal(ContentState.Unreadable, Read("bad.pst", bytes).State);
    }

    [Fact]
    public void A_file_cut_short_is_damaged_not_a_crash()
    {
        var bytes = Mailbox(Payroll(), Payroll("Other")).Build();
        var r = Read("cut.pst", bytes[..(bytes.Length / 2)]);
        Assert.Equal(ContentState.Unreadable, r.State);
    }

    [Theory]
    [InlineData("Unicode")]
    [InlineData("Ansi")]
    [InlineData("Unicode4K")]
    public void Random_damage_never_escapes_as_an_unexpected_error(string layout)
    {
        var format = Enum.Parse<PstFormat>(layout);
        var builder = new PstBuilder(format, crypt: 2) { TwoLevelSubnodeTrees = true, TwoLevelDataTrees = true };
        var good = Mailbox(builder,
            Payroll(),
            new PstMessage { Subject = "Long", Body = new string('x', 20_000) },
            new PstMessage { Subject = "Files", Html = "<p>Hi</p>", Attachments = [PstAttachment.File("a.csv", "x,y"), PstAttachment.Attached(Payroll("Inner"))] }).Build();
        var random = new Random(11);
        for (var round = 0; round < 1_000; round++)
        {
            var bytes = (byte[])good.Clone();
            // Mostly past the header, so the damage reaches the trees, blocks and heaps; sometimes the header too.
            for (var flips = random.Next(1, 8); flips > 0; flips--)
            {
                var at = random.Next(5) == 0 ? random.Next(4, 600) : random.Next(0x4400, bytes.Length);
                bytes[at] ^= (byte)random.Next(1, 256);
            }
            var r = OutlookDataFileReader.Read(new MemoryStream(bytes), limits, depth: 0, CancellationToken.None);
            Assert.True(Enum.IsDefined(r.State));
        }
    }

    // ---------- limits ----------

    [Fact]
    public void Reading_stops_at_the_mailbox_character_budget_and_says_so()
    {
        limits = new ContentLimits { MaxChars = 1_000, MaxMailboxChars = 1_000 };
        var messages = Enumerable.Range(0, 50).Select(i => new PstMessage { Subject = $"Message {i}", Body = new string('a', 80) }).ToArray();
        var r = Read("mail.pst", Mailbox(messages).Build());
        Assert.Equal(ContentState.Partial, r.State);
        Assert.Matches(@"^Read \d+ of 50 messages in the mailbox: stopped at the 1,000 character limit\.$", r.Note);
        Assert.True(r.Text!.Length <= 1_000);
    }

    [Fact]
    public void The_mailbox_budget_is_larger_than_one_files()
    {
        limits = new ContentLimits { MaxChars = 1_000, MaxMailboxChars = 5_000 };
        var messages = Enumerable.Range(0, 40).Select(i => new PstMessage { Subject = $"Message {i}", Body = new string('a', 80) }).ToArray();
        var r = Read("mail.pst", Mailbox(messages).Build());
        Assert.Equal(ContentState.Scanned, r.State);
        Assert.True(r.Text!.Length > 1_000);
    }

    [Fact]
    public void Reading_stops_before_the_files_time_runs_out()
    {
        limits = new ContentLimits { PerFileTimeout = TimeSpan.FromTicks(1) };
        var bytes = Mailbox(Payroll(), Payroll("Other")).Build();
        var r = OutlookDataFileReader.Read(new MemoryStream(bytes), limits, depth: 0, CancellationToken.None);
        Assert.Equal(ContentState.TimedOut, r.State);
        Assert.Equal("The time for reading this file ran out before any message in the mailbox was read.", r.Note);
    }

    [Fact]
    public void Cancelling_stops_the_reading()
    {
        var bytes = Mailbox(Payroll()).Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OutlookDataFileReader.Read(new MemoryStream(bytes), limits, depth: 0, cts.Token));
    }

    [Fact]
    public void The_file_is_never_changed()
    {
        var path = Path.Combine(dir, "mail.pst");
        File.WriteAllBytes(path, Mailbox(Payroll()).Build());
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        new FileContentReader(limits).Read(path, CancellationToken.None);
        Assert.Equal(before.Item1, File.ReadAllBytes(path));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Mailbox_limits_are_validated_and_raised_for_full_scans()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentLimits { MaxMailboxChars = 999 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentLimits { MaxMailboxChars = 50_000_001 }.Validate());
        Assert.Equal(5_000_000, new ContentLimits().MailboxChars);
        Assert.Equal(20_000_000, new ContentLimits().ForFullScan().MailboxChars);
        Assert.Equal(30_000_000, new ContentLimits { MaxMailboxChars = 30_000_000 }.ForFullScan().MailboxChars);
        // A mailbox never gets less room than one file.
        Assert.Equal(2_000_000, new ContentLimits { MaxChars = 2_000_000, MaxMailboxChars = 1_000 }.MailboxChars);
    }

    /// <summary>Recomputes a Unicode node tree page's checksum after a test has changed it.</summary>
    private static void RewriteChecksum(byte[] bytes, long page)
    {
        var at = (int)page;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 500), PstFile.Crc(bytes.AsSpan(at, 496)));
    }
}
