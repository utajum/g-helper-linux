using System.Reflection;
using System.Runtime.InteropServices;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Ensures native libraries (libSkiaSharp.so, libHarfBuzzSharp.so) can be found at runtime.
/// 
/// Search order:
///   1. Same directory as the binary (normal case when installed to /opt/ghelper/)
///   2. ~/.cache/ghelper/libs/ (extracted from embedded resources)
///   3. System library paths (LD_LIBRARY_PATH, /usr/lib, etc.)
/// 
/// Must be called BEFORE any SkiaSharp/Avalonia code runs.
/// </summary>
public static class NativeLibExtractor
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "ghelper", "libs");

    private static readonly string[] NativeLibs = ["libHarfBuzzSharp.so", "libSkiaSharp.so"];

    private static readonly Dictionary<string, IntPtr> _loadedLibs = new();

    /// <summary>
    /// Find, extract if needed, preload native libraries, and register a DLL import resolver.
    /// Must be called BEFORE any SkiaSharp/Avalonia code runs.
    /// </summary>
    public static void ExtractAndLoad()
    {
        // Determine where the binary lives
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";

        foreach (var lib in NativeLibs)
        {
            IntPtr handle = IntPtr.Zero;

            // Strategy 1: Look next to the binary
            var nextToBinary = Path.Combine(exeDir, lib);
            if (File.Exists(nextToBinary))
            {
                handle = NativeLibrary.Load(nextToBinary);
            }

            // Strategy 2: Look in cache dir (previously extracted)
            if (handle == IntPtr.Zero)
            {
                var cached = Path.Combine(CacheDir, lib);
                if (File.Exists(cached))
                {
                    try { handle = NativeLibrary.Load(cached); } catch { }
                }
            }

            // Strategy 3: Try extracting from embedded resources
            if (handle == IntPtr.Zero)
            {
                var extracted = ExtractFromResources(lib);
                if (extracted != null)
                {
                    try { handle = NativeLibrary.Load(extracted); } catch { }
                }
            }

            // Strategy 4: Let the system find it (LD_LIBRARY_PATH, /usr/lib, etc.)
            if (handle == IntPtr.Zero)
            {
                try { handle = NativeLibrary.Load(lib); } catch { }
            }

            if (handle != IntPtr.Zero)
            {
                var libName = Path.GetFileNameWithoutExtension(lib); // "libSkiaSharp"
                _loadedLibs[libName] = handle;
                _loadedLibs[lib] = handle;
            }
        }

        // Register resolver for all assemblies that might P/Invoke these libs
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SkiaSharp.SKPaint).Assembly, ResolveNativeLib);
        }
        catch { }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(HarfBuzzSharp.Blob).Assembly, ResolveNativeLib);
        }
        catch { }
    }

    private static IntPtr ResolveNativeLib(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Try exact match first ("libSkiaSharp" or "libSkiaSharp.so")
        if (_loadedLibs.TryGetValue(libraryName, out var handle))
            return handle;

        // Try with .so suffix
        if (_loadedLibs.TryGetValue(libraryName + ".so", out handle))
            return handle;

        // Fall back to default resolution
        return IntPtr.Zero;
    }

    private static string? ExtractFromResources(string resourceName)
    {
        try
        {
            using var stream = typeof(NativeLibExtractor).Assembly
                .GetManifestResourceStream(resourceName);

            if (stream == null) return null;

            Directory.CreateDirectory(CacheDir);
            var targetPath = Path.Combine(CacheDir, resourceName);

            using var fs = File.Create(targetPath);
            stream.CopyTo(fs);

            // Make executable
#pragma warning disable CA1416
            File.SetUnixFileMode(targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

            return targetPath;
        }
        catch
        {
            return null;
        }
    }
}
