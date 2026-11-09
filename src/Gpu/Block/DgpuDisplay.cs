using System;
using System.Collections.Generic;
using System.IO;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu.Block;

/// <summary>External displays on the dGPU: the automatic switch must not blank them.</summary>
public static class DgpuDisplay
{
    /// <summary>Connectors that are not physical outputs.</summary>
    private static readonly string[] NonPhysical = { "Virtual-", "Unknown-", "Writeback-" };

    /// <summary>Internal panels: they never keep the dGPU visible.</summary>
    private static readonly string[] InternalPanels = { "eDP-", "LVDS-", "DSI-", "DPI-", "SPI-" };

    /// <summary>True for a physical external output such as "HDMI-A-3".</summary>
    public static bool IsExternalConnector(string connector)
    {
        if (string.IsNullOrEmpty(connector))
            return false;
        foreach (var p in NonPhysical)
            if (connector.StartsWith(p, StringComparison.Ordinal))
                return false;
        foreach (var p in InternalPanels)
            if (connector.StartsWith(p, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>Pure core: is an external display in this connector list ("card2-HDMI-A-3").</summary>
    public static bool AnyExternalConnected(int card, IEnumerable<(string Name, string Status)> connectors)
    {
        string prefix = $"card{card}-";
        foreach (var (name, status) in connectors)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (!IsExternalConnector(name.Substring(prefix.Length)))
                continue;
            if (status.Trim() == "connected")
                return true;
        }
        return false;
    }

    /// <summary>DRM card numbers owned by a PCI device.</summary>
    public static List<int> CardNumbers(string bdf)
    {
        var cards = new List<int>();
        try
        {
            string drm = $"/sys/bus/pci/devices/{bdf}/drm";
            if (!Directory.Exists(drm))
                return cards;
            foreach (var dir in Directory.EnumerateDirectories(drm))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith("card", StringComparison.Ordinal))
                    continue;
                if (int.TryParse(name.Substring(4), out int n))
                    cards.Add(n);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"DgpuDisplay: card scan failed: {ex.Message}");
        }
        return cards;
    }

    /// <summary>Test seam: models the topology instead of the host monitors.</summary>
    internal static Func<bool>? TestOverride { get; set; }

    /// <summary>True when an external display is on the dGPU; false when the topology is unreadable.</summary>
    public static bool HasExternalDisplay(string? bdf = null)
    {
        if (TestOverride != null)
            return TestOverride();

        try
        {
            bdf ??= DgpuPowerMonitor.DiscreteBdf();
            if (bdf == null)
                return false;

            var cards = CardNumbers(bdf);
            if (cards.Count == 0)
                return false;

            var connectors = new List<(string, string)>();
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/drm"))
            {
                string name = Path.GetFileName(dir);
                string statusPath = Path.Combine(dir, "status");
                if (!File.Exists(statusPath))
                    continue;
                connectors.Add((name, SysfsHelper.ReadAttributeSilent(statusPath) ?? ""));
            }

            foreach (int card in cards)
                if (AnyExternalConnected(card, connectors))
                    return true;
            return false;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"DgpuDisplay: probe failed: {ex.Message}");
            return false;
        }
    }
}
