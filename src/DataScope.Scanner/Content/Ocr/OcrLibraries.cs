using System.Reflection;
using System.Runtime.InteropServices;

namespace DataScope.Scanner.Content.Ocr;

/// <summary>
/// Finds the text recognition libraries (D129). On Windows, Tesseract and Leptonica ship with Data Scope in the
/// <c>ocr</c> folder next to the worker and are loaded from there only, never from the search path. On Linux, used for
/// development and tests, they are the system's own (<c>apt install libtesseract5</c>).
/// </summary>
internal static partial class OcrLibraries
{
    /// <summary>Library names the P/Invoke declarations use; <see cref="Resolve"/> turns them into files.</summary>
    internal const string Tesseract = "datascope-tesseract";

    internal const string Leptonica = "datascope-leptonica";

    internal const string Folder = "ocr";
    internal const string WindowsTesseract = "tesseract55.dll";
    internal const string WindowsLeptonica = "leptonica-1.85.0.dll";

    private static readonly string[] LinuxTesseract = ["libtesseract.so.5"];
    private static readonly string[] LinuxLeptonica = ["libleptonica.so.6", "liblept.so.5"];

    /// <summary>The Microsoft Visual C++ runtime the Windows libraries are built against (2015-2022, x64).</summary>
    internal static readonly string[] VisualCRuntime = ["msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"];

    private static readonly Lock Gate = new();
    private static bool registered;

    /// <summary>Makes the names above resolve. Every other library of this assembly loads as usual.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (!registered)
            {
                NativeLibrary.SetDllImportResolver(typeof(OcrLibraries).Assembly, Resolve);
                registered = true;
            }
        }
    }

    internal static string Directory => Path.Combine(AppContext.BaseDirectory, Folder);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) => name switch
    {
        Tesseract => OperatingSystem.IsWindows() ? LoadWindows(WindowsTesseract) : LoadSystem(LinuxTesseract),
        Leptonica => OperatingSystem.IsWindows() ? LoadWindows(WindowsLeptonica) : LoadSystem(LinuxLeptonica),
        _ => IntPtr.Zero,
    };

    /// <summary>
    /// Leptonica first, by its full path: Tesseract imports it by name, and Windows then finds the copy already loaded
    /// instead of searching for one.
    /// </summary>
    private static IntPtr LoadWindows(string file)
    {
        var leptonica = NativeLibrary.Load(Path.Combine(Directory, WindowsLeptonica));
        return file == WindowsLeptonica ? leptonica : NativeLibrary.Load(Path.Combine(Directory, file));
    }

    private static IntPtr LoadSystem(string[] names)
    {
        // Linux builds of Tesseract use OpenMP, whose threads spin while they wait: on a busy computer one image took
        // forty times longer (docs/BENCHMARKS.md). One thread, as on Windows, whose build has no OpenMP. Read when the
        // library loads, so it is set first; a value already set by an administrator is kept.
        _ = setenv("OMP_THREAD_LIMIT", "1", 0);
        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                return handle;
            }
        }
        throw new DllNotFoundException($"None of {string.Join(", ", names)} is installed.");
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);

    /// <summary>Why the libraries can't load, in words for an administrator. Only called after loading failed.</summary>
    internal static string Unavailable(Exception ex)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Text in images is not read on this computer: Tesseract 5 is not installed (on Ubuntu, apt install libtesseract5).";
        }
        if (!File.Exists(Path.Combine(Directory, WindowsTesseract)) || !File.Exists(Path.Combine(Directory, WindowsLeptonica)))
        {
            return $"Text in images is not read on this server: Data Scope's text recognition files are missing from {Directory}. Run the installer again to repair it.";
        }
        var system = Environment.SystemDirectory;
        if (VisualCRuntime.Any(f => !File.Exists(Path.Combine(system, f))))
        {
            return "Text in images is not read on this server: it needs the Microsoft Visual C++ Redistributable (2015-2022, x64). Install it, and the next scan reads these images.";
        }
        return $"Text in images is not read on this server: the text recognition libraries did not load ({ex.GetType().Name}).";
    }
}
