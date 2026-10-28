using DataScope.Core.Classification;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using Kind = DataScope.Scanner.Content.FileContentReader.Kind;

namespace DataScope.Scanner.Content;

/// <summary>
/// Reads the files inside 7-Zip and RAR archives (D132), with SharpCompress, exactly as <see cref="ArchiveReader"/>
/// reads a zip: each file with the same reader and limits as on a share, decompressed into memory and never written to
/// disk, within the archive's budget of unpacked bytes, entry count and nesting depth. Files are read in the order
/// they are stored, so a solid archive (one compressed stream for many files) is decompressed once; in a solid archive
/// the files skipped count towards the budget too, since they are decompressed to reach the next.
/// </summary>
internal static class PackedArchiveReader
{
    internal static readonly byte[] SevenZipMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    internal static readonly byte[] RarMagic = "Rar!\x1A\x07"u8.ToArray();

    public static bool IsPacked(ReadOnlySpan<byte> head) => head.StartsWith(SevenZipMagic) || head.StartsWith(RarMagic);

    public static ContentExtraction Read(Stream stream, ContentLimits limits, int depth, CancellationToken ct)
    {
        Span<byte> head = stackalloc byte[8];
        var headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        var sevenZip = head[..headLength].StartsWith(SevenZipMagic);
        var container = sevenZip ? "7-Zip archive" : "RAR archive";
        var options = new ReaderOptions { LeaveStreamOpen = true };

        IArchive archive;
        List<IArchiveEntry> entries;
        bool sequential, complete;
        try
        {
            archive = sevenZip ? SevenZipArchive.OpenArchive(stream, options) : RarArchive.OpenArchive(stream, options);
            entries = [];
            foreach (var entry in archive.Entries)
            {
                if (!entry.IsDirectory)
                {
                    entries.Add(entry);
                }
                if (entries.Count > limits.MaxArchiveEntries)
                {
                    archive.Dispose();
                    return ContentExtraction.Of(ContentState.Unreadable, $"The {container} holds more than {limits.MaxArchiveEntries:N0} files, the safety limit.");
                }
            }
            // 7-Zip, and RAR archives packed as one compressed stream ("solid"), are read in order: reaching a file
            // means decompressing the ones before it, so skipped files count towards the budget too.
            sequential = sevenZip || archive.IsSolid;
            complete = archive.IsComplete;
        }
        catch (CryptographicException)
        {
            return ContentExtraction.Of(ContentState.Encrypted, $"The {container} is password-protected, including its list of files.");
        }
        catch (Exception ex) when (Damaged(ex))
        {
            return ContentExtraction.Of(ContentState.Unreadable, $"The {container} is damaged or not in the format its name suggests.");
        }

        using (archive)
        {
            var inner = new InnerFiles(limits, depth);
            // A solid stream with password-protected files in it can't be stepped through without the password.
            if (sequential && entries.Any(e => e.IsEncrypted))
            {
                foreach (var entry in entries)
                {
                    if (Readable(entry, inner, limits, out _, out _))
                    {
                        inner.Encrypted(); // Not protected itself, but behind protected files in the same stream.
                    }
                }
                return inner.Result(container);
            }
            try
            {
                var bytesLeft = limits.MaxUncompressedBytes;
                foreach (var (entry, open) in InOrder(archive, sequential))
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                    {
                        continue;
                    }
                    if (inner.Stopped)
                    {
                        break;
                    }
                    var size = Math.Max(0, entry.Size);
                    if (!Readable(entry, inner, limits, out var name, out var take))
                    {
                        if (sequential && (bytesLeft -= size) < 0)
                        {
                            inner.Stop();
                            break;
                        }
                        continue;
                    }
                    if ((sequential ? size : take) > bytesLeft)
                    {
                        inner.Stop();
                        break;
                    }
                    MemoryStream content;
                    try
                    {
                        content = Extract(open, size, take, ct);
                    }
                    catch (Exception ex) when (ex is ContentLimitException || Damaged(ex))
                    {
                        // Damaged, or expanding beyond its declared size (a decompression bomb).
                        inner.Failed();
                        if (sequential)
                        {
                            inner.Stop(); // The rest of one compressed stream can't be reached past a broken file.
                            break;
                        }
                        continue;
                    }
                    bytesLeft -= sequential ? size : content.Length;
                    using (content)
                    {
                        inner.Read(name, content, truncated: take < size, ct);
                    }
                }
            }
            catch (CryptographicException)
            {
                inner.Encrypted();
                inner.Stop();
            }
            catch (Exception ex) when (Damaged(ex))
            {
                inner.Failed();
                inner.Stop();
            }
            if (!complete)
            {
                inner.Stop(); // One part of an archive split into several files: the other parts are not opened.
            }
            return inner.Result(container);
        }
    }

    /// <summary>The files with a way to open each: in stored order through one reader, or one by one.</summary>
    private static IEnumerable<(IEntry Entry, Func<Stream> Open)> InOrder(IArchive archive, bool sequential)
    {
        if (!sequential)
        {
            foreach (var entry in archive.Entries)
            {
                yield return (entry, entry.OpenEntryStream);
            }
            yield break;
        }
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            yield return (reader.Entry, reader.OpenEntryStream);
        }
    }

    /// <summary>Whether an entry is read, and how much of it: text and copies only from the start, as in a zip.</summary>
    private static bool Readable(IEntry entry, InnerFiles inner, ContentLimits limits, out string name, out long take)
    {
        name = Path.GetFileName(entry.Key?.Replace('\\', '/') ?? "") is { Length: > 0 } n ? n : "file";
        take = 0;
        var kind = FileContentReader.KindOf(name);
        if (FileContentReader.NeverOpened(kind))
        {
            inner.NotRead();
            return false;
        }
        if (entry.IsEncrypted)
        {
            inner.Encrypted();
            return false;
        }
        var size = Math.Max(0, entry.Size);
        var readOnlyStart = kind is Kind.Text or Kind.ByContents;
        if (!readOnlyStart && size > FileContentReader.MaxBytes(kind, limits))
        {
            inner.TooLarge();
            return false;
        }
        take = readOnlyStart ? Math.Min(size, limits.MaxTextBytes) : size;
        return true;
    }

    /// <summary>The first <paramref name="take"/> bytes of an entry, which may not expand beyond <paramref name="size"/>.</summary>
    private static MemoryStream Extract(Func<Stream> open, long size, long take, CancellationToken ct)
    {
        var content = new MemoryStream((int)Math.Min(take, 1 << 20));
        using var source = new LimitedStream(open(), size);
        var buffer = new byte[81920];
        while (content.Length < take)
        {
            ct.ThrowIfCancellationRequested();
            var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, take - content.Length));
            if (n == 0)
            {
                break;
            }
            content.Write(buffer, 0, n);
        }
        content.Position = 0;
        return content;
    }

    /// <summary>What SharpCompress and the decompressors throw for a damaged or unsupported archive.</summary>
    private static bool Damaged(Exception ex) =>
        ex is SharpCompressException or InvalidDataException or EndOfStreamException or NotSupportedException
            or IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException;
}
