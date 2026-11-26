using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Extracts embedded native libraries and helper executables to
/// ~/.cache/ghelper/libs/ and preloads the libraries before SkiaSharp/
/// Avalonia initialises.
///
/// Rewrites cached files only when their bytes differ from the payload,
/// without any version stamping.
///
/// Every extraction outcome is logged via <see cref="Logger"/> so the
/// startup banner and the diagnostics dump show which resources made it
/// to disk, where they came from, and how big they ended up.
/// </summary>
public static class NativeLibExtractor
{
    private static readonly string HomeDir =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string CacheDir = Path.Combine(
        HomeDir, ".cache", "ghelper", "libs");

    private static readonly string[] NativeLibs = ["libHarfBuzzSharp.so", "libSkiaSharp.so"];

    /// <summary>
    /// Replace the user's home prefix with "~/" so log lines stay concise
    /// and don't leak the actual username into shared diagnostic dumps.
    /// </summary>
    private static string Short(string path)
    {
        if (!string.IsNullOrEmpty(HomeDir) && path.StartsWith(HomeDir, StringComparison.Ordinal))
        {
            var tail = path.Substring(HomeDir.Length).TrimStart('/');
            return tail.Length == 0 ? "~" : "~/" + tail;
        }
        return path;
    }

    private static readonly Dictionary<string, IntPtr> _loadedLibs = new();

    public static void ExtractAndLoad()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        Logger.WriteLine($"NativeLibExtractor: starting (cache={Short(CacheDir)})");

        foreach (var lib in NativeLibs)
        {
            IntPtr handle = IntPtr.Zero;
            string loadSource = "none";

            var nextToBinary = Path.Combine(exeDir, lib);
            if (File.Exists(nextToBinary))
            {
                try
                {
                    handle = NativeLibrary.Load(nextToBinary);
                    loadSource = "exe-dir";
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"NativeLibExtractor: {lib} load from exe-dir failed: {ex.Message}");
                }
            }

            if (handle == IntPtr.Zero)
            {
                var extracted = ExtractFromResources(lib);
                if (extracted != null)
                {
                    try
                    {
                        handle = NativeLibrary.Load(extracted);
                        loadSource = "embedded";
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"NativeLibExtractor: {lib} load from cache failed: {ex.Message}");
                    }
                }
            }

            if (handle == IntPtr.Zero)
            {
                try
                {
                    handle = NativeLibrary.Load(lib);
                    loadSource = "system";
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"NativeLibExtractor: {lib} load from system path failed: {ex.Message}");
                }
            }

            if (handle != IntPtr.Zero)
            {
                var libName = Path.GetFileNameWithoutExtension(lib);
                _loadedLibs[libName] = handle;
                _loadedLibs[lib] = handle;
                Logger.WriteLine($"NativeLibExtractor: loaded {lib} from {loadSource}");
            }
            else
            {
                Logger.WriteLine($"NativeLibExtractor: ERROR could not load {lib} from any source");
            }
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SkiaSharp.SKPaint).Assembly, ResolveNativeLib);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"NativeLibExtractor: SkiaSharp resolver hook failed: {ex.Message}");
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(HarfBuzzSharp.Blob).Assembly, ResolveNativeLib);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"NativeLibExtractor: HarfBuzz resolver hook failed: {ex.Message}");
        }

        Logger.WriteLine("NativeLibExtractor: done");
    }

    /// <summary>
    /// Find an embedded tool binary (e.g. wlr-randr, ghelper-audio).
    /// Extracts (when changed) if the resource exists; falls back to the
    /// cached copy or the system PATH otherwise.
    /// </summary>
    public static string? FindTool(string toolName)
    {
        var extracted = ExtractFromResources(toolName);
        if (extracted != null)
            return extracted;

        var cached = Path.Combine(CacheDir, toolName);
        if (File.Exists(cached))
        {
            Logger.WriteLine($"NativeLibExtractor: FindTool({toolName}) using cached copy {Short(cached)}");
            return cached;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = toolName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var path = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                if (proc.ExitCode == 0 && path.Length > 0)
                {
                    Logger.WriteLine($"NativeLibExtractor: FindTool({toolName}) resolved via PATH at {Short(path)}");
                    return path;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"NativeLibExtractor: FindTool({toolName}) PATH lookup failed: {ex.Message}");
        }

        Logger.WriteLine($"NativeLibExtractor: FindTool({toolName}) not found");
        return null;
    }

    private static IntPtr ResolveNativeLib(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_loadedLibs.TryGetValue(libraryName, out var handle))
            return handle;
        if (_loadedLibs.TryGetValue(libraryName + ".so", out handle))
            return handle;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Extract an embedded resource to the cache directory unless the cached
    /// copy is already identical.
    /// Logs and returns null on failure; on ETXTBSY (cached binary still
    /// executing) returns the existing stale path so the caller can fall
    /// back to spawning what is already on disk.
    /// </summary>
    private static string? ExtractFromResources(string resourceName)
    {
        var asm = typeof(NativeLibExtractor).Assembly;
        var targetPath = Path.Combine(CacheDir, resourceName);

        Stream? stream = null;
        try
        {
            stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Logger.WriteLine($"NativeLibExtractor: resource {resourceName} not embedded; skipping");
                return null;
            }

            Directory.CreateDirectory(CacheDir);
            long expectedLength = stream.Length;

            // Rewrite (and chmod below) when the cached copy lost its exec bit.
#pragma warning disable CA1416
            if (SameContent(targetPath, stream)
                && File.GetUnixFileMode(targetPath).HasFlag(UnixFileMode.UserExecute))
            {
                Logger.WriteLine($"NativeLibExtractor: {resourceName} up to date at {Short(targetPath)}");
                return targetPath;
            }
#pragma warning restore CA1416

            try
            {
                using var fs = File.Create(targetPath);
                stream.CopyTo(fs);
                if (fs.Length != expectedLength)
                {
                    Logger.WriteLine($"NativeLibExtractor: short write for {resourceName} ({fs.Length}/{expectedLength}) at {Short(targetPath)}");
                    return null;
                }
            }
            catch (IOException ex)
            {
                bool busy = ex.Message.Contains("Text file busy", StringComparison.OrdinalIgnoreCase)
                         || ex.Message.Contains("ETXTBSY", StringComparison.OrdinalIgnoreCase);
                Logger.WriteLine(busy
                    ? $"NativeLibExtractor: {resourceName} is busy (ETXTBSY); falling back to stale {Short(targetPath)}"
                    : $"NativeLibExtractor: IO error writing {resourceName} to {Short(targetPath)}: {ex.Message}");
                return File.Exists(targetPath) ? targetPath : null;
            }

#pragma warning disable CA1416
            File.SetUnixFileMode(targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

            Logger.WriteLine($"NativeLibExtractor: extracted {resourceName} -> {Short(targetPath)} ({FormatSize(expectedLength)})");
            return targetPath;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"NativeLibExtractor: extract failed for {resourceName}: {ex.Message}");
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>True when the file at <paramref name="path"/> already holds
    /// the stream's bytes. Rewinds the stream either way.</summary>
    private static bool SameContent(string path, Stream stream)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length != stream.Length)
                return false;
            var a = new byte[1 << 16];
            var b = new byte[1 << 16];
            int n;
            while ((n = stream.Read(a, 0, a.Length)) > 0)
            {
                fs.ReadExactly(b, 0, n);
                if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n)))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    /// <summary>
    /// Human-readable byte size used in log messages so operators can
    /// eyeball whether the expected payload sits on disk. Bytes / KiB /
    /// MiB granularity is plenty for our ~150 KB - ~10 MB range.
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KiB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MiB";
    }
}
