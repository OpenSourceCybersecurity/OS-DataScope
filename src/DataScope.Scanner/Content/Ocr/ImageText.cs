using System.Diagnostics;
using System.Text;

namespace DataScope.Scanner.Content.Ocr;

/// <summary>How reading the text in one encoded image went.</summary>
internal enum ImageOutcome
{
    /// <summary>Recognition ran; <see cref="ImageRead.Text"/> may still be empty when the picture holds no text.</summary>
    Read,

    /// <summary>Too small to hold readable text, so not recognised.</summary>
    TooSmall,

    /// <summary>More pixels than <see cref="ContentLimits.MaxImagePixels"/>: not decoded.</summary>
    TooManyPixels,

    /// <summary>This platform has no decoder for the format.</summary>
    NoDecoder,

    Damaged,

    /// <summary>The file's time for text recognition was used up before this image.</summary>
    OutOfTime,
}

/// <param name="PagesRead">Pages recognised (a multi-page TIFF has several).</param>
/// <param name="MorePages">True when pages were left unread because of the page limit.</param>
/// <param name="Stopped">True when time ran out part way through.</param>
internal sealed record ImageRead(ImageOutcome Outcome, string Text, int PagesRead, bool MorePages, bool Stopped, int Confidence, int Width, int Height)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Reads the text in pictures (D129): images on file shares, scanned pages in PDFs, and images inside zips and saved
/// emails. Runs in the separate reader process (D127): the decoders and the recogniser see untrusted bytes, and that
/// process has memory and time limits of its own. Images are decoded by the platform (<see cref="ImageDecoders"/>),
/// refused above <see cref="ContentLimits.MaxImagePixels"/> from their headers before any decoding, scaled to a size
/// recognition reads well and quickly, and recognised by <see cref="TesseractEngine"/> within the time allowed.
/// </summary>
internal static class ImageText
{
    /// <summary>Pictures narrower or shorter than this hold no readable line of text.</summary>
    internal const int MinWidth = 24;
    internal const int MinHeight = 12;

    /// <summary>Larger pictures are shrunk to this many pixels: plenty for a full page at 300 dpi, and faster.</summary>
    internal const long MaxRecognisedPixels = 12_000_000;

    /// <summary>
    /// Smaller pictures, such as screenshots at screen resolution, are enlarged twice over: Tesseract misreads text
    /// whose letters are only a few pixels high.
    /// </summary>
    internal const long EnlargeBelowPixels = 2_000_000;

    private static readonly Lock Gate = new();
    private static TesseractEngine? engine;
    private static string? unavailable;
    private static IImageDecoder? decoder;

    [ThreadStatic]
    private static FileClock? clock;

    /// <summary>
    /// Starts the clock for one file's text recognition: all its pictures (pages of a scanned PDF, images in a zip)
    /// share <see cref="ContentLimits.OcrAllowed"/>, which stops short of the file's own time limit so the text read
    /// so far is kept rather than the whole file timing out.
    /// </summary>
    public static IDisposable StartFile(ContentLimits limits) => clock = new FileClock(limits.OcrAllowed, clock);

    /// <summary>Time left for this file; a fresh allowance when no file clock is running (a reader called directly).</summary>
    private static TimeSpan Remaining(ContentLimits limits) => clock is { } c ? c.Allowed - c.Watch.Elapsed : limits.OcrAllowed;

    /// <summary>
    /// Why text in pictures can't be read here (turned off, or the libraries or model are missing), or null when it
    /// can. Checked once per process: the libraries don't appear while it runs.
    /// </summary>
    public static string? Unavailable(ContentLimits limits)
    {
        if (!limits.ReadTextInImages)
        {
            return "Reading text in images is turned off in this server's settings (Scanner:Content:ReadTextInImages).";
        }
        lock (Gate)
        {
            Start();
            return unavailable;
        }
    }

    /// <summary>
    /// Recognises the text in an encoded picture: up to <paramref name="maxPages"/> pages of it. Call
    /// <see cref="Unavailable"/> first.
    /// </summary>
    public static ImageRead Read(ReadOnlyMemory<byte> encoded, int maxPages, ContentLimits limits, CancellationToken ct)
    {
        lock (Gate)
        {
            Start();
            if (engine is null || decoder is null)
            {
                throw new InvalidOperationException(unavailable);
            }
            IImageFrames frames;
            try
            {
                frames = decoder.Open(encoded);
            }
            catch (ImageDecodeException ex)
            {
                return Result(ex.Unsupported ? ImageOutcome.NoDecoder : ImageOutcome.Damaged);
            }
            using (frames)
            {
                return Recognize(engine, frames, maxPages, limits, ct);
            }
        }
    }

    private static ImageRead Recognize(TesseractEngine engine, IImageFrames frames, int maxPages, ContentLimits limits, CancellationToken ct)
    {
        var text = new StringBuilder();
        int width = 0, height = 0, pages = 0, recognised = 0;
        long confidence = 0, weight = 0;
        var stopped = false;
        var tooSmall = false;
        try
        {
            for (var page = 0; page < maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                if (frames.Size(page) is not var (w, h))
                {
                    break;
                }
                if (page == 0)
                {
                    (width, height) = (w, h);
                }
                if ((long)w * h > limits.MaxImagePixels)
                {
                    if (page == 0)
                    {
                        return Result(ImageOutcome.TooManyPixels, width: w, height: h);
                    }
                    break;
                }
                pages++;
                if (w < MinWidth || h < MinHeight)
                {
                    tooSmall = true;
                    continue;
                }
                var left = Remaining(limits);
                if (left <= TimeSpan.Zero)
                {
                    if (recognised == 0)
                    {
                        return Result(ImageOutcome.OutOfTime, width: width, height: height);
                    }
                    stopped = true;
                    pages--;
                    break;
                }
                var (tw, th) = RecognitionSize(w, h);
                var read = RecognizeLevel(engine, frames.Read(page, tw, th), left, limits, ct);
                recognised++;
                if (!string.IsNullOrWhiteSpace(read.Text))
                {
                    text.Append(read.Text.Trim()).Append('\n');
                    confidence += (long)read.Confidence * read.Text.Length;
                    weight += read.Text.Length;
                }
                if (read.Stopped)
                {
                    stopped = true;
                    break;
                }
            }
        }
        catch (ImageDecodeException)
        {
            if (recognised == 0)
            {
                return Result(ImageOutcome.Damaged, width: width, height: height);
            }
            stopped = true; // A damaged later page: keep what the earlier ones said.
        }
        if (recognised == 0 && tooSmall)
        {
            return Result(ImageOutcome.TooSmall, pages: pages, width: width, height: height);
        }
        var more = !stopped && pages == maxPages && frames.Size(maxPages) is not null;
        return new ImageRead(ImageOutcome.Read, text.ToString(), pages, more, stopped, weight == 0 ? 0 : (int)(confidence / weight), width, height);
    }

    /// <summary>
    /// Reads a page turned level and upright (D131). When the reading is poor and the page looks like lines of text,
    /// it is read again turned the other ways <see cref="PageGeometry"/> thinks likely, while the file's time lasts,
    /// and the best reading is kept. Pages that read well, and pictures that are not text, are read once.
    /// </summary>
    private static RecognizedText RecognizeLevel(TesseractEngine engine, GrayImage image, TimeSpan left, ContentLimits limits, CancellationToken ct)
    {
        var layout = PageGeometry.Find(image);
        var best = engine.Recognize(PageGeometry.Straighten(image, layout.Best), left, ct);
        for (var i = 1; i < layout.Tries.Count && layout.TextLike && !best.Stopped && !ReadsWell(best); i++)
        {
            var remaining = Remaining(limits);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }
            var other = engine.Recognize(PageGeometry.Straighten(image, layout.Tries[i]), remaining, ct);
            if (Quality(other) > Quality(best))
            {
                best = other;
            }
            else if (other.Stopped)
            {
                break; // The time ran out on a worse reading: keep the better one, whole.
            }
        }
        return best;
    }

    /// <summary>A reading this sure, with this many letters and digits, is not tried again turned another way.</summary>
    internal const int WellReadConfidence = 60;
    internal const int WellReadCharacters = 12;

    internal static bool ReadsWell(RecognizedText read) => read.Confidence >= WellReadConfidence && LettersAndDigits(read.Text) >= WellReadCharacters;

    /// <summary>Upside-down and sideways text reads as a few unsure scraps; the right way up, as many sure words.</summary>
    private static long Quality(RecognizedText read) => (long)LettersAndDigits(read.Text) * read.Confidence;

    private static int LettersAndDigits(string text) => text.Count(char.IsLetterOrDigit);

    /// <summary>The size a picture is recognised at: shrunk when huge, enlarged when small, keeping its shape.</summary>
    internal static (int Width, int Height) RecognitionSize(int width, int height)
    {
        var pixels = (long)width * height;
        var scale = pixels > MaxRecognisedPixels ? Math.Sqrt((double)MaxRecognisedPixels / pixels)
            : pixels < EnlargeBelowPixels ? Math.Min(2.0, Math.Sqrt((double)MaxRecognisedPixels / pixels))
            : 1.0;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static ImageRead Result(ImageOutcome outcome, int pages = 0, int width = 0, int height = 0) =>
        new(outcome, "", pages, false, false, 0, width, height);

    /// <summary>Loads the engine once per process, or remembers why it can't.</summary>
    private static void Start()
    {
        if (engine is not null || unavailable is not null)
        {
            return;
        }
        try
        {
            engine = TesseractEngine.Create();
            decoder = ImageDecoders.ForThisPlatform();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException
            or TypeInitializationException or IOException)
        {
            engine?.Dispose();
            engine = null;
            unavailable = ex is InvalidOperationException or IOException
                ? $"Text in images is not read on this server: {ex.Message} Run the installer again to repair it."
                : OcrLibraries.Unavailable(ex);
        }
    }

    /// <summary>One file's time for text recognition. Files inside it (a zip's images) share it.</summary>
    private sealed class FileClock(TimeSpan allowed, FileClock? outer) : IDisposable
    {
        public TimeSpan Allowed { get; } = outer is null ? allowed : outer.Allowed - outer.Watch.Elapsed;

        public Stopwatch Watch { get; } = Stopwatch.StartNew();

        public void Dispose() => clock = outer;
    }
}
