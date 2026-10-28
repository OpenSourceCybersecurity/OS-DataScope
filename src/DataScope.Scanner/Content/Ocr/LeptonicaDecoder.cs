using System.Runtime.InteropServices;

namespace DataScope.Scanner.Content.Ocr;

/// <summary>
/// Decodes images with Leptonica, the image library Tesseract is built on, on platforms other than Windows
/// (development and tests). Windows uses <see cref="WicDecoder"/> instead, so the image parsers that face customer
/// files there are the operating system's own.
/// </summary>
internal sealed unsafe partial class LeptonicaDecoder : IImageDecoder
{
    private const int SeverityNone = 6;

    static LeptonicaDecoder()
    {
        OcrLibraries.Register();
        _ = setMsgSeverity(SeverityNone); // Leptonica writes errors to standard error otherwise; nobody reads them.
    }

    public IImageFrames Open(ReadOnlyMemory<byte> encoded) => new Frames(encoded);

    private sealed class Frames(ReadOnlyMemory<byte> encoded) : IImageFrames
    {
        private readonly Dictionary<int, IntPtr> pages = [];

        public (int Width, int Height)? Size(int page)
        {
            if (page == 0 && !pages.ContainsKey(0))
            {
                // The header alone, so an image claiming to be enormous is refused before anything is decoded.
                int w, h;
                fixed (byte* data = encoded.Span)
                {
                    if (pixReadHeaderMem(data, (nuint)encoded.Length, out _, out w, out h, out _, out _, out _) != 0)
                    {
                        throw new ImageDecodeException("damaged", unsupported: false);
                    }
                }
                return (w, h);
            }
            var pix = Page(page);
            return pix == IntPtr.Zero ? null : (pixGetWidth(pix), pixGetHeight(pix));
        }

        public GrayImage Read(int page, int width, int height)
        {
            var pix = Page(page);
            if (pix == IntPtr.Zero)
            {
                throw new ImageDecodeException("damaged", unsupported: false);
            }
            var opaque = pixRemoveAlpha(pix); // Over white: transparent areas would otherwise read as black.
            var gray = pixConvertTo8(opaque, 0);
            var scaled = gray != IntPtr.Zero && (pixGetWidth(gray) != width || pixGetHeight(gray) != height)
                ? pixScaleToSize(gray, width, height) : pixClone(gray);
            var bytes = scaled == IntPtr.Zero ? IntPtr.Zero : pixEndianByteSwapNew(scaled);
            try
            {
                if (bytes == IntPtr.Zero || pixGetDepth(bytes) != 8)
                {
                    throw new ImageDecodeException("damaged", unsupported: false);
                }
                var w = pixGetWidth(bytes);
                var h = pixGetHeight(bytes);
                var stride = pixGetWpl(bytes) * 4;
                var source = (byte*)pixGetData(bytes);
                var pixels = new byte[w * h];
                for (var y = 0; y < h; y++)
                {
                    new ReadOnlySpan<byte>(source + (long)y * stride, w).CopyTo(pixels.AsSpan(y * w, w));
                }
                return new GrayImage(pixels, w, h);
            }
            finally
            {
                Destroy(ref bytes);
                Destroy(ref scaled);
                Destroy(ref gray);
                Destroy(ref opaque);
            }
        }

        /// <summary>A page decoded, kept until disposed. Pages after the first exist only in TIFF images.</summary>
        private IntPtr Page(int page)
        {
            if (pages.TryGetValue(page, out var pix))
            {
                return pix;
            }
            fixed (byte* data = encoded.Span)
            {
                pix = page == 0 ? pixReadMem(data, (nuint)encoded.Length) : pixReadMemTiff(data, (nuint)encoded.Length, page);
            }
            if (pix == IntPtr.Zero && page == 0)
            {
                throw new ImageDecodeException("damaged", unsupported: false);
            }
            pages[page] = pix;
            return pix;
        }

        public void Dispose()
        {
            foreach (var key in pages.Keys.ToList())
            {
                var pix = pages[key];
                Destroy(ref pix);
            }
            pages.Clear();
        }
    }

    private static void Destroy(ref IntPtr pix)
    {
        if (pix != IntPtr.Zero)
        {
            pixDestroy(ref pix);
        }
    }

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial int setMsgSeverity(int severity);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial int pixReadHeaderMem(byte* data, nuint size, out int format, out int width, out int height, out int bitsPerSample, out int samplesPerPixel, out int colormapped);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial IntPtr pixReadMem(byte* data, nuint size);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial IntPtr pixReadMemTiff(byte* data, nuint size, int page);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial IntPtr pixRemoveAlpha(IntPtr pix);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial IntPtr pixConvertTo8(IntPtr pix, int colormap);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial IntPtr pixScaleToSize(IntPtr pix, int width, int height);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial IntPtr pixClone(IntPtr pix);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial IntPtr pixEndianByteSwapNew(IntPtr pix);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial int pixGetWidth(IntPtr pix);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial int pixGetHeight(IntPtr pix);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial int pixGetDepth(IntPtr pix);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial int pixGetWpl(IntPtr pix);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial IntPtr pixGetData(IntPtr pix);

    [LibraryImport(OcrLibraries.Leptonica)]
    private static partial void pixDestroy(ref IntPtr pix);
}
