using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DataScope.Scanner.Content.Ocr;

/// <summary>What text recognition read from one picture.</summary>
/// <param name="Confidence">Tesseract's mean word confidence, 0 to 100.</param>
/// <param name="Stopped">True when the time allowed ran out, so only part of the picture was read.</param>
internal readonly record struct RecognizedText(string Text, int Confidence, bool Stopped);

/// <summary>
/// Tesseract's text recognition (D129), through its C interface. One engine per reader process, used by one file at
/// a time. The English model (tessdata_fast) ships with Data Scope and is loaded from memory, so nothing is fetched
/// and no other model or configuration file on the server is consulted.
/// </summary>
internal sealed unsafe partial class TesseractEngine : IDisposable
{
    internal const string ModelFolder = "tessdata";
    internal const string ModelFile = "eng.traineddata";

    private const int LstmOnly = 1;
    private const int AutomaticPageSegmentation = 3;

    private IntPtr handle;

    /// <summary>The scan's cancellation, read by <see cref="Cancel"/> while recognition runs. One file at a time.</summary>
    private static CancellationToken running;

    private TesseractEngine(IntPtr handle) => this.handle = handle;

    public string Version { get; private init; } = "";

    /// <exception cref="DllNotFoundException">The libraries are not there or don't load.</exception>
    /// <exception cref="InvalidOperationException">The model is missing or did not load.</exception>
    public static TesseractEngine Create()
    {
        OcrLibraries.Register();
        var modelPath = Path.Combine(AppContext.BaseDirectory, ModelFolder, ModelFile);
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException($"The text recognition model {modelPath} is missing.");
        }
        var model = File.ReadAllBytes(modelPath);
        var api = TessBaseAPICreate();
        if (api == IntPtr.Zero)
        {
            throw new InvalidOperationException("Tesseract did not start.");
        }
        var engine = new TesseractEngine(api) { Version = Marshal.PtrToStringUTF8(TessVersion()) ?? "" };
        try
        {
            int status;
            fixed (byte* data = model)
            fixed (byte* language = "eng\0"u8)
            {
                status = TessBaseAPIInit5(api, data, model.Length, language, LstmOnly, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, 0, 0);
            }
            if (status != 0)
            {
                throw new InvalidOperationException("The text recognition model did not load.");
            }
            // Tesseract's messages would go to standard error, where nobody reads them.
            TessBaseAPISetVariable(api, "debug_file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
            TessBaseAPISetPageSegMode(api, AutomaticPageSegmentation);
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>Reads the text in <paramref name="image"/>, stopping when <paramref name="allowed"/> runs out or the scan stops.</summary>
    public RecognizedText Recognize(GrayImage image, TimeSpan allowed, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        var monitor = TessMonitorCreate();
        var clock = Stopwatch.StartNew();
        try
        {
            running = ct;
            TessMonitorSetCancelFunc(monitor, &Cancel);
            TessMonitorSetDeadlineMSecs(monitor, (int)Math.Clamp(allowed.TotalMilliseconds, 1, int.MaxValue));
            fixed (byte* pixels = image.Pixels)
            {
                TessBaseAPISetImage(handle, pixels, image.Width, image.Height, 1, image.Width);
                TessBaseAPISetSourceResolution(handle, 300);
                var status = TessBaseAPIRecognize(handle, monitor);
                var stopped = clock.Elapsed >= allowed;
                ct.ThrowIfCancellationRequested();
                if (status != 0 && !stopped)
                {
                    // Not stopped by the clock: Tesseract found no lines of text to read.
                    TessBaseAPIClear(handle);
                    return new RecognizedText("", 0, false);
                }
                var text = TessBaseAPIGetUTF8Text(handle);
                var read = text == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(text) ?? "";
                if (text != IntPtr.Zero)
                {
                    TessDeleteText(text);
                }
                var confidence = Math.Clamp(TessBaseAPIMeanTextConf(handle), 0, 100);
                TessBaseAPIClear(handle);
                return new RecognizedText(read, confidence, stopped);
            }
        }
        finally
        {
            running = default;
            TessMonitorDelete(monitor);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Cancel(IntPtr self, int words) => running.IsCancellationRequested ? (byte)1 : (byte)0;

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            TessBaseAPIEnd(handle);
            TessBaseAPIDelete(handle);
            handle = IntPtr.Zero;
        }
    }

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial IntPtr TessVersion();

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial IntPtr TessBaseAPICreate();

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessBaseAPIDelete(IntPtr api);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial int TessBaseAPIInit5(IntPtr api, byte* data, int size, byte* language, int mode, IntPtr configs, int configCount,
        IntPtr names, IntPtr values, nuint variableCount, int onlyNonDebug);

    [LibraryImport(OcrLibraries.Tesseract, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int TessBaseAPISetVariable(IntPtr api, string name, string value);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessBaseAPISetPageSegMode(IntPtr api, int mode);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessBaseAPISetImage(IntPtr api, byte* pixels, int width, int height, int bytesPerPixel, int bytesPerLine);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessBaseAPISetSourceResolution(IntPtr api, int ppi);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial int TessBaseAPIRecognize(IntPtr api, IntPtr monitor);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial IntPtr TessBaseAPIGetUTF8Text(IntPtr api);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessDeleteText(IntPtr text);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial int TessBaseAPIMeanTextConf(IntPtr api);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessBaseAPIClear(IntPtr api);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessBaseAPIEnd(IntPtr api);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial IntPtr TessMonitorCreate();

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessMonitorDelete(IntPtr monitor);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessMonitorSetCancelFunc(IntPtr monitor, delegate* unmanaged[Cdecl]<IntPtr, int, byte> cancel);

    [LibraryImport(OcrLibraries.Tesseract)]
    private static partial void TessMonitorSetDeadlineMSecs(IntPtr monitor, int milliseconds);
}
