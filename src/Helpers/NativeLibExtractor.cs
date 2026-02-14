using System.Reflection;
using System.Runtime.InteropServices;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Extracts embedded native libraries (.so) to a cache directory and preloads them.
/// This enables single-binary deployment — libSkiaSharp.so and libHarfBuzzSharp.so
/// are embedded as resources and extracted on first run.
/// </summary>
public static class NativeLibExtractor
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "ghelper-linux", "libs");

    private static readonly string[] NativeLibs = ["libHarfBuzzSharp.so", "libSkiaSharp.so"];

    private static readonly Dictionary<string, IntPtr> _loadedLibs = new();

    /// <summary>
    /// Extract embedded native libraries, preload them, and register a DLL import resolver.
    /// Must be called BEFORE any SkiaSharp/Avalonia code runs.
    /// </summary>
    public static void ExtractAndLoad()
    {
        Directory.CreateDirectory(CacheDir);

        var versionFile = Path.Combine(CacheDir, ".version");
        var currentVersion = GetBinaryVersion();
        var cachedVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";

        var needsExtract = cachedVersion != currentVersion;

        foreach (var lib in NativeLibs)
        {
            var targetPath = Path.Combine(CacheDir, lib);

            if (needsExtract || !File.Exists(targetPath))
            {
                ExtractResource(lib, targetPath);
            }

            // Load and cache the handle so the resolver can return it
            var handle = NativeLibrary.Load(targetPath);
            var libName = Path.GetFileNameWithoutExtension(lib); // "libSkiaSharp"
            _loadedLibs[libName] = handle;
            _loadedLibs[lib] = handle; // also map "libSkiaSharp.so"
        }

        if (needsExtract)
        {
            File.WriteAllText(versionFile, currentVersion);
        }

        // Register resolver for all assemblies that might P/Invoke these libs
        NativeLibrary.SetDllImportResolver(typeof(SkiaSharp.SKPaint).Assembly, ResolveNativeLib);
        NativeLibrary.SetDllImportResolver(typeof(HarfBuzzSharp.Blob).Assembly, ResolveNativeLib);
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

    private static void ExtractResource(string resourceName, string targetPath)
    {
        using var stream = typeof(NativeLibExtractor).Assembly
            .GetManifestResourceStream(resourceName);

        if (stream == null)
            throw new FileNotFoundException(
                $"Embedded native library '{resourceName}' not found. " +
                "The binary may have been built without embedded native libs.");

        using var fs = File.Create(targetPath);
        stream.CopyTo(fs);

        // Make executable (chmod 755) — Linux-only project
#pragma warning disable CA1416
        File.SetUnixFileMode(targetPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
    }

    private static string GetBinaryVersion()
    {
        // Use the binary's last write time as a version fingerprint.
        // This ensures libs are re-extracted after an update.
        var exePath = Environment.ProcessPath;
        if (exePath != null && File.Exists(exePath))
        {
            var info = new FileInfo(exePath);
            return $"{info.Length}-{info.LastWriteTimeUtc:yyyyMMddHHmmss}";
        }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    }
}
