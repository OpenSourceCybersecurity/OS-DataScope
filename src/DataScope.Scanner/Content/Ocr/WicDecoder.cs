using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DataScope.Scanner.Content.Ocr;

/// <summary>
/// Decodes images with the Windows Imaging Component, the decoders built into Windows (JPEG, PNG, TIFF, GIF, BMP,
/// and HEIC or WebP where their Windows extensions are installed). They are part of the operating system, so Windows
/// Update keeps them patched; the image parsers bundled with Tesseract are never given a customer's file (D129).
/// Called through the COM interfaces' function tables directly: index numbers are the methods' places in wincodec.h.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WicDecoder : IImageDecoder
{
    private static readonly Guid FactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid FactoryInterface = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid Bgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    private const int ComponentNotFound = unchecked((int)0x88982F50);
    private const int UnknownImageFormat = unchecked((int)0x88982F07);
    private const int ChangedMode = unchecked((int)0x80010106);

    public IImageFrames Open(ReadOnlyMemory<byte> encoded)
    {
        var frames = new Frames();
        try
        {
            frames.Load(encoded.Span);
            return frames;
        }
        catch
        {
            frames.Dispose();
            throw;
        }
    }

    private sealed class Frames : IImageFrames
    {
        private bool comStarted;
        private IntPtr factory;
        private IntPtr stream;
        private IntPtr decoder;
        private uint count;

        public void Load(ReadOnlySpan<byte> encoded)
        {
            var hr = CoInitializeEx(IntPtr.Zero, 0); // Multithreaded; a thread already set up either way is fine too.
            comStarted = hr >= 0;
            if (hr < 0 && hr != ChangedMode)
            {
                throw new ImageDecodeException($"COM 0x{hr:X8}", unsupported: false);
            }
            var factoryClass = FactoryClass;
            var factoryInterface = FactoryInterface;
            Check(CoCreateInstance(&factoryClass, IntPtr.Zero, 1, &factoryInterface, out factory));
            fixed (byte* data = encoded)
            {
                stream = SHCreateMemStream(data, checked((uint)encoded.Length)); // Its own copy.
            }
            if (stream == IntPtr.Zero)
            {
                throw new ImageDecodeException("memory", unsupported: false);
            }
            IntPtr created;
            var hrDecoder = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr*, int>)Method(factory, 4))(factory, stream, null, 0, &created);
            if (hrDecoder is ComponentNotFound or UnknownImageFormat)
            {
                throw new ImageDecodeException("no decoder", unsupported: true);
            }
            Check(hrDecoder);
            decoder = created;
            uint frames;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Method(decoder, 12))(decoder, &frames));
            count = frames;
        }

        public (int Width, int Height)? Size(int page)
        {
            if (page < 0 || page >= count)
            {
                return null;
            }
            var frame = Frame(page);
            try
            {
                uint w, h;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)Method(frame, 3))(frame, &w, &h));
                return (checked((int)w), checked((int)h));
            }
            finally
            {
                Release(frame);
            }
        }

        public GrayImage Read(int page, int width, int height)
        {
            var frame = Frame(page);
            IntPtr converter = IntPtr.Zero, scaler = IntPtr.Zero;
            try
            {
                // Pixels are pulled through the chain row by row; the full-size picture is never held as 32-bit colour.
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Method(factory, 10))(factory, &converter));
                var format = Bgra32;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)Method(converter, 8))(converter, frame, &format, 0, IntPtr.Zero, 0, 0));
                var source = converter;
                uint w, h;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)Method(frame, 3))(frame, &w, &h));
                if (w != width || h != height)
                {
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Method(factory, 11))(factory, &scaler));
                    var mode = width < w ? 3 : 2; // Fant to shrink, cubic to enlarge.
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int, int>)Method(scaler, 8))(scaler, converter, (uint)width, (uint)height, mode));
                    source = scaler;
                }
                var stride = checked((uint)width * 4);
                var size = checked(stride * (uint)height);
                var bgra = GC.AllocateUninitializedArray<byte>((int)size);
                fixed (byte* buffer = bgra)
                {
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, uint, byte*, int>)Method(source, 7))(source, null, stride, size, buffer));
                }
                return new GrayImage(ImageDecoders.GrayOverWhite(bgra, width * height), width, height);
            }
            finally
            {
                Release(scaler);
                Release(converter);
                Release(frame);
            }
        }

        private IntPtr Frame(int page)
        {
            IntPtr frame;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Method(decoder, 13))(decoder, (uint)page, &frame));
            return frame;
        }

        public void Dispose()
        {
            Release(decoder);
            Release(stream);
            Release(factory);
            decoder = stream = factory = IntPtr.Zero;
            if (comStarted)
            {
                CoUninitialize();
                comStarted = false;
            }
        }
    }

    private static IntPtr Method(IntPtr instance, int index) => (*(IntPtr**)instance)[index];

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Method(instance, 2))(instance);
        }
    }

    /// <summary>Any failure after the decoder accepted the format means the image is damaged.</summary>
    private static void Check(int hr)
    {
        if (hr < 0)
        {
            throw new ImageDecodeException($"WIC 0x{hr:X8}", unsupported: false);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* classId, IntPtr outer, uint context, Guid* interfaceId, out IntPtr instance);

    [LibraryImport("shlwapi.dll")]
    private static partial IntPtr SHCreateMemStream(byte* data, uint size);
}
