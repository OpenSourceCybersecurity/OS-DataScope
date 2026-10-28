namespace DataScope.Scanner.Content.Ocr;

/// <summary>An 8-bit grayscale picture, one byte per pixel, row after row: what text recognition reads.</summary>
internal sealed record GrayImage(byte[] Pixels, int Width, int Height);

/// <summary>Opens encoded images (JPEG, PNG, TIFF, GIF, BMP and whatever else the platform decodes).</summary>
internal interface IImageDecoder
{
    /// <exception cref="ImageDecodeException">Not an image this decoder reads, or damaged.</exception>
    IImageFrames Open(ReadOnlyMemory<byte> encoded);
}

/// <summary>The pages of one image (a multi-page TIFF has several; everything else has one).</summary>
internal interface IImageFrames : IDisposable
{
    /// <summary>The size of a page, read from its header without decoding it; null when there is no such page.</summary>
    /// <exception cref="ImageDecodeException">The page is damaged.</exception>
    (int Width, int Height)? Size(int page);

    /// <summary>Decodes a page and scales it to <paramref name="width"/> by <paramref name="height"/>, as grayscale on white.</summary>
    /// <exception cref="ImageDecodeException">The page is damaged.</exception>
    GrayImage Read(int page, int width, int height);
}

internal sealed class ImageDecodeException(string message, bool unsupported) : Exception(message)
{
    /// <summary>True when this platform has no decoder for the format; false when the image is damaged.</summary>
    public bool Unsupported { get; } = unsupported;
}

/// <summary>The platform's decoder: Windows' own (WIC, kept patched by Windows Update) or Leptonica elsewhere.</summary>
internal static class ImageDecoders
{
    public static IImageDecoder ForThisPlatform() => OperatingSystem.IsWindows() ? new WicDecoder() : new LeptonicaDecoder();

    /// <summary>
    /// Grayscale from 32-bit BGRA, laid over white so transparent areas don't turn black and hide dark text.
    /// </summary>
    public static byte[] GrayOverWhite(ReadOnlySpan<byte> bgra, int pixels)
    {
        var gray = new byte[pixels];
        for (var i = 0; i < pixels; i++)
        {
            var p = bgra.Slice(i * 4, 4);
            var luma = (29 * p[0] + 150 * p[1] + 77 * p[2] + 128) >> 8;
            var alpha = p[3];
            gray[i] = (byte)((luma * alpha + 255 * (255 - alpha) + 127) / 255);
        }
        return gray;
    }
}
