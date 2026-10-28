using System.IO.Compression;
using DataScope.Core.Classification;
using DataScope.Scanner.Content.Ocr;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace DataScope.Scanner.Content;

/// <summary>
/// Extracts the text layer of a PDF with PdfPig. Every decompression goes through <see cref="BoundedFilterProvider"/>,
/// which measures a stream's decoded size before decoding it, so a compression bomb is refused instead of filling
/// memory. A page with next to no text in its text layer is a scanned or photographed page: its pictures are read
/// by OCR (D129), within the file's time for text recognition.
/// </summary>
internal static class PdfReader
{
    /// <summary>A page whose text layer holds fewer letters and digits than this has its pictures read by OCR.</summary>
    internal const int MinTextLayerCharacters = 20;

    /// <summary>Pictures on one page read by OCR, largest first: a scanned page is one picture, or a few strips.</summary>
    internal const int MaxPicturesPerPage = 8;

    public static ContentExtraction Read(Stream stream, ContentLimits limits, CancellationToken ct)
    {
        Span<byte> head = stackalloc byte[1024];
        var headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (head[..headLength].IndexOf("%PDF-"u8) < 0)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The file is not a valid PDF.");
        }

        var filters = new BoundedFilterProvider(limits.MaxUncompressedBytes);
        var options = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true, FilterProvider = filters };
        var budget = new TextBudget(limits.MaxChars);
        var ocr = new PageOcr(limits);
        int pages;
        int read;
        try
        {
            using var document = PdfDocument.Open(stream, options);
            pages = document.NumberOfPages;
            read = Math.Min(pages, limits.MaxPdfPages);
            for (var i = 1; i <= read && !budget.Full; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = document.GetPage(i);
                var text = ContentOrderTextExtractor.GetText(page);
                budget.Append(text);
                budget.Append('\n');
                if (text.Count(char.IsLetterOrDigit) < MinTextLayerCharacters)
                {
                    ocr.Read(page, budget, ct);
                    if (filters.LimitHit)
                    {
                        break;
                    }
                }
            }
        }
        catch (PdfDocumentEncryptedException)
        {
            return ContentExtraction.Of(ContentState.Encrypted, "The PDF is password-protected.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !filters.LimitHit)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "The PDF is damaged or uses features Data Scope cannot read.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "A compressed part of the PDF expands beyond the safety limit.");
        }

        // The parser can recover from a refused stream and carry on, so a refusal it swallowed still counts.
        if (filters.LimitHit)
        {
            return ContentExtraction.Of(ContentState.Unreadable, "A compressed part of the PDF expands beyond the safety limit.");
        }
        var all = budget.ToString();
        if (pages > 0 && string.IsNullOrWhiteSpace(all))
        {
            return ocr.Nothing();
        }
        if (budget.Full || read < pages)
        {
            var why = read < pages ? $"Only the first {read:N0} of {pages:N0} pages were scanned." : $"Only the first {all.Length:N0} characters were scanned.";
            return new ContentExtraction(ContentState.Partial, all, ocr.Note is { } also ? $"{why} {also}" : why);
        }
        return new ContentExtraction(ocr.Missed ? ContentState.Partial : ContentState.Scanned, all, ocr.Note);
    }

    /// <summary>OCR of the pages that are pictures, and what to say about it.</summary>
    private sealed class PageOcr(ContentLimits limits)
    {
        private string? unavailable;
        private int pictures;
        private int pagesRead;
        private int pagesWithText;
        private int undecodable;
        private int tooLarge;
        private int outOfTime;
        private long confidence;
        private long weight;

        /// <summary>True when page pictures were left unread: the file is only partly scanned.</summary>
        public bool Missed => unavailable is not null || undecodable > 0 || tooLarge > 0 || outOfTime > 0;

        public void Read(Page page, TextBudget budget, CancellationToken ct)
        {
            var candidates = page.GetImages()
                .Where(i => i.WidthInSamples >= ImageText.MinWidth && i.HeightInSamples >= ImageText.MinHeight)
                .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
                .Take(MaxPicturesPerPage)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }
            pictures += candidates.Count;
            if ((unavailable ??= ImageText.Unavailable(limits)) is not null)
            {
                return;
            }
            if (outOfTime > 0)
            {
                outOfTime++; // Counted per page left unread.
                return;
            }
            var anyRead = false;
            var anyText = false;
            foreach (var picture in candidates)
            {
                if ((long)picture.WidthInSamples * picture.HeightInSamples > limits.MaxImagePixels)
                {
                    tooLarge++;
                    continue;
                }
                if (Encoded(picture) is not { } encoded)
                {
                    undecodable++;
                    continue;
                }
                var result = ImageText.Read(encoded, 1, limits, ct);
                switch (result.Outcome)
                {
                    case ImageOutcome.Read:
                        anyRead = true;
                        if (result.HasText)
                        {
                            anyText = true;
                            budget.Append(result.Text);
                            budget.Append('\n');
                            confidence += (long)result.Confidence * result.Text.Length;
                            weight += result.Text.Length;
                        }
                        if (result.Stopped)
                        {
                            outOfTime++;
                            return;
                        }
                        break;
                    case ImageOutcome.TooSmall:
                        break;
                    case ImageOutcome.TooManyPixels:
                        tooLarge++;
                        break;
                    case ImageOutcome.OutOfTime:
                        outOfTime++;
                        return;
                    default:
                        undecodable++;
                        break;
                }
            }
            pagesRead += anyRead ? 1 : 0;
            pagesWithText += anyText ? 1 : 0;
        }

        /// <summary>What to say when the PDF gave no text at all.</summary>
        public ContentExtraction Nothing()
        {
            if (pictures == 0)
            {
                return ContentExtraction.Of(ContentState.Unsupported, "The PDF has no text and no pictures that could hold text.");
            }
            if (unavailable is not null)
            {
                return ContentExtraction.Of(ContentState.NotRead, $"The PDF's pages are pictures. {unavailable}");
            }
            if (pagesRead == 0 && outOfTime > 0)
            {
                return ContentExtraction.Of(ContentState.TimedOut, "The time for reading text in the PDF's pictures ran out before any was read.");
            }
            if (pagesRead == 0)
            {
                return ContentExtraction.Of(tooLarge > 0 && undecodable == 0 ? ContentState.TooLarge : ContentState.Unsupported,
                    $"The PDF's pages are pictures that could not be read: {Problems()}.");
            }
            return new ContentExtraction(Missed ? ContentState.Partial : ContentState.Scanned, null,
                Missed ? $"OCR found no text in the PDF's page pictures, and some were not read: {Problems()}." : "OCR found no text in the PDF's page pictures.");
        }

        /// <summary>The note for a PDF with text, when OCR had a part in it.</summary>
        public string? Note
        {
            get
            {
                var parts = new List<string>();
                if (pagesWithText > 0)
                {
                    var pages = pagesWithText == 1 ? "1 page" : $"{pagesWithText:N0} pages";
                    parts.Add($"Text on {pages} was read from pictures by OCR (average confidence {(weight == 0 ? 0 : confidence / weight)}%), so a character may have been misread.");
                }
                if (Missed)
                {
                    parts.Add($"Some page pictures were not read: {Problems()}.");
                }
                return parts.Count == 0 ? null : string.Join(" ", parts);
            }
        }

        private string Problems()
        {
            var parts = new List<string>();
            if (unavailable is not null)
            {
                parts.Add(unavailable.TrimEnd('.'));
            }
            if (outOfTime > 0)
            {
                parts.Add($"the {FileContentReader.Seconds(limits.OcrAllowed)} limit for reading text in pictures was reached");
            }
            if (tooLarge > 0)
            {
                parts.Add($"{tooLarge:N0} over the {limits.MaxImagePixels / 1_000_000.0:0.#} million pixel limit");
            }
            if (undecodable > 0)
            {
                parts.Add($"{undecodable:N0} could not be decoded (damaged, or JBIG2 or JPEG 2000, which Data Scope does not decode)");
            }
            return string.Join("; ", parts);
        }
    }

    /// <summary>
    /// A PDF picture as an image file: a JPEG as stored (DCTDecode), anything else PdfPig can decode as a PNG. JBIG2
    /// and JPEG 2000 pictures are not decoded. Decoding goes through the bounded filters, like the rest of the PDF.
    /// </summary>
    private static byte[]? Encoded(IPdfImage picture)
    {
        var filters = Filters(picture.ImageDictionary);
        if (filters.Count == 1 && filters[0] is "DCTDecode" or "DCT")
        {
            return picture.RawMemory.ToArray();
        }
        if (filters.Any(f => f is "JPXDecode" or "JBIG2Decode"))
        {
            return null;
        }
        return picture.TryGetPng(out var png) ? png : null;
    }

    private static List<string> Filters(DictionaryToken dictionary)
    {
        if (!dictionary.TryGet(NameToken.Filter, out var token) && !dictionary.TryGet(NameToken.F, out token))
        {
            return [];
        }
        return token switch
        {
            NameToken name => [name.Data],
            ArrayToken array => array.Data.OfType<NameToken>().Select(n => n.Data).ToList(),
            _ => [],
        };
    }

    /// <summary>Wraps PdfPig's filters with a shared budget of decoded bytes for the whole document.</summary>
    internal sealed class BoundedFilterProvider(long maxDecodedBytes) : IFilterProvider
    {
        private readonly IFilterProvider inner = DefaultFilterProvider.Instance;
        private long remaining = maxDecodedBytes;

        public bool LimitHit { get; private set; }

        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(inner.GetFilters(dictionary));

        public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(inner.GetNamedFilters(names));

        public IReadOnlyList<IFilter> GetAllFilters() => Wrap(inner.GetAllFilters());

        private List<IFilter> Wrap(IReadOnlyList<IFilter> filters) => filters.Select(f => (IFilter)new BoundedFilter(f, this)).ToList();

        internal void Reserve(long bytes)
        {
            if (bytes > remaining)
            {
                LimitHit = true;
                throw new ContentLimitException("A compressed stream expands beyond the safety limit.");
            }
            remaining -= bytes;
        }

        private sealed class BoundedFilter(IFilter inner, BoundedFilterProvider owner) : IFilter
        {
            public bool IsSupported => inner.IsSupported;

            public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
            {
                // Measure first: the decoded size is known before any large buffer exists.
                var expected = inner switch
                {
                    FlateFilter => InflatedSize(input.Span, owner.remaining),
                    LzwFilter => input.Length * 4096L,
                    RunLengthFilter => RunLengthSize(input.Span),
                    _ => input.Length,
                };
                owner.Reserve(expected);
                var output = inner.Decode(input, streamDictionary, filterProvider, filterIndex);
                // Give back what was over-reserved, and refuse anything that came out bigger than measured.
                owner.Reserve(output.Length - expected);
                return output;
            }
        }

        /// <summary>Inflates into a scratch buffer, counting bytes, and stops one past the limit.</summary>
        internal static long InflatedSize(ReadOnlySpan<byte> input, long limit)
        {
            // PDF writers are inconsistent about the two-byte zlib header; PdfPig skips it, so do the same.
            var body = input.Length > 2 ? input[2..].ToArray() : [];
            using var deflate = new DeflateStream(new MemoryStream(body), CompressionMode.Decompress);
            var scratch = new byte[81920];
            long total = 0;
            try
            {
                int n;
                while ((n = deflate.Read(scratch)) > 0)
                {
                    total += n;
                    if (total > limit)
                    {
                        return total;
                    }
                }
            }
            catch (InvalidDataException)
            {
                // Damaged data: PdfPig decodes what it can, which is no more than was counted here.
            }
            return total;
        }

        internal static long RunLengthSize(ReadOnlySpan<byte> input)
        {
            long total = 0;
            var i = 0;
            while (i < input.Length)
            {
                var length = input[i];
                if (length == 128)
                {
                    break;
                }
                if (length < 128)
                {
                    total += length + 1;
                    i += length + 2;
                }
                else
                {
                    total += 257 - length;
                    i += 2;
                }
            }
            return total;
        }
    }
}
