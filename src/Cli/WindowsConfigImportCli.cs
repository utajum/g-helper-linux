using System.Text;
using System.Text.Json;

namespace GHelper.Linux.Cli;

/// <summary>
/// One-shot importer for the original Windows G-Helper config.json.
///
/// The Linux port intentionally uses the same JSON key/value format for most
/// settings. This importer preserves those keys and only translates values
/// whose representation differs on Linux (currently calibrated fan maxima).
/// Unknown/Windows-only keys are retained harmlessly so future Linux support
/// can pick them up without requiring another migration.
/// </summary>
public static class WindowsConfigImportCli
{
    private const string ImportFlag = "--import-windows-config";
    private const string TargetFlag = "--import-target";
    private const string DryRunFlag = "--dry-run";

    public static int? TryDispatch(string[] args)
    {
        int importIndex = Array.IndexOf(args, ImportFlag);
        if (importIndex < 0)
            return null;

        if (importIndex + 1 >= args.Length || args[importIndex + 1].StartsWith("--", StringComparison.Ordinal))
        {
            PrintUsage();
            return 2;
        }

        string source = Path.GetFullPath(args[importIndex + 1]);
        string target = GetDefaultTarget();

        int targetIndex = Array.IndexOf(args, TargetFlag);
        if (targetIndex >= 0)
        {
            if (targetIndex + 1 >= args.Length || args[targetIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                PrintUsage();
                return 2;
            }

            target = Path.GetFullPath(args[targetIndex + 1]);
        }

        bool dryRun = args.Contains(DryRunFlag, StringComparer.Ordinal);

        try
        {
            return Import(source, target, dryRun);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Import failed: {ex.Message}");
            return 1;
        }
    }

    private static int Import(string source, string target, bool dryRun)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("Windows G-Helper config.json was not found.", source);

        var windows = ReadObject(source);
        if (windows.Count == 0)
            throw new InvalidDataException("Source config is empty or is not a JSON object.");

        Dictionary<string, JsonElement> merged = File.Exists(target)
            ? ReadObject(target)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        int existingCount = merged.Count;

        // Windows and Linux G-Helper share the same config key format. Source
        // values intentionally win so custom modes/fan curves are migrated.
        foreach (var pair in windows)
            merged[pair.Key] = pair.Value.Clone();

        string? fanMax = ConvertFanCalibration(windows);
        if (fanMax != null)
            merged["fan_max"] = JsonString(fanMax);

        // Marker is useful for diagnostics and is ignored by upstream builds.
        merged["mehdi_windows_config_imported"] = JsonNumber(1);
        merged["mehdi_windows_config_source"] = JsonString(Path.GetFileName(source));

        Console.WriteLine("G-Helper Windows -> Linux configuration import");
        Console.WriteLine($"  Source: {source}");
        Console.WriteLine($"  Target: {target}");
        Console.WriteLine($"  Windows keys: {windows.Count}");
        Console.WriteLine($"  Existing Linux keys: {existingCount}");
        Console.WriteLine($"  Result keys: {merged.Count}");

        if (fanMax != null)
            Console.WriteLine($"  Fan calibration: {fanMax} RPM (CPU,GPU,Mid)");
        else
            Console.WriteLine("  Fan calibration: no complete Windows fan_max_0/1/2 set found");

        Console.WriteLine("  Note: Windows-only GameVisual/Splendid settings may remain in JSON but are ignored on Linux.");

        if (dryRun)
        {
            Console.WriteLine("Dry run: no files were changed.");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("Target config has no parent directory."));

        if (File.Exists(target))
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backup = $"{target}.pre-windows-import-{stamp}.bak";
            File.Copy(target, backup, overwrite: false);
            Console.WriteLine($"  Backup: {backup}");
        }

        WriteObjectAtomic(target, merged);
        Console.WriteLine("Import complete. Start G-Helper normally to review the imported settings.");
        return 0;
    }

    private static Dictionary<string, JsonElement> ReadObject(string path)
    {
        string json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} does not contain a JSON object.");

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }

    private static string? ConvertFanCalibration(IReadOnlyDictionary<string, JsonElement> source)
    {
        if (!TryGetInt(source, "fan_max_0", out int cpu)
            || !TryGetInt(source, "fan_max_1", out int gpu)
            || !TryGetInt(source, "fan_max_2", out int mid))
            return null;

        int[] values = [cpu, gpu, mid];

        // Windows G-Helper historically stores the EC fan value in units of
        // 100 RPM (e.g. 76 == 7600 RPM), whereas Linux hwmon exposes actual
        // RPM. If a config already contains realistic RPM values, keep them.
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > 0 && values[i] <= 104)
                values[i] *= 100;

            if (values[i] < 1000 || values[i] > 15000)
                return null;
        }

        return string.Join(",", values);
    }

    private static bool TryGetInt(
        IReadOnlyDictionary<string, JsonElement> source,
        string key,
        out int value)
    {
        value = 0;
        if (!source.TryGetValue(key, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            return true;

        return element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), out value);
    }

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement JsonNumber(int value)
    {
        using var document = JsonDocument.Parse(value.ToString());
        return document.RootElement.Clone();
    }

    private static void WriteObjectAtomic(string target, Dictionary<string, JsonElement> data)
    {
        string temp = target + ".tmp";
        var options = new JsonWriterOptions { Indented = true };

        using (var stream = File.Create(temp))
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            foreach (var pair in data.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        File.Move(temp, target, overwrite: true);
    }

    private static string GetDefaultTarget()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "ghelper", "config.json");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: ghelper --import-windows-config <config.json> [--import-target <path>] [--dry-run]");
    }
}
