using Avalonia.Media;
using GHelper.Linux.Helpers;
using GHelper.Linux.Peripherals.Logitech;
using GHelper.Linux.Peripherals.Logitech.Models;
using HidSharp;

namespace GHelper.Linux.Peripherals;

/// <summary>
/// Central registry for connected peripheral devices (ASUS + Logitech).
/// Detects, connects, and manages mice (keyboards planned).
/// </summary>
public static class PeripheralsProvider
{
    private static readonly object _lock = new();

    public static List<IMousePeripheral> ConnectedMice { get; } = new();

    /// <summary>Fires when a peripheral is added or removed.</summary>
    public static event Action? DeviceChanged;

    public static bool IsAuraSync
    {
        get => AppConfig.Is("aura_sync_mouse");
        set => AppConfig.Set("aura_sync_mouse", value ? 1 : 0);
    }

    /// <summary>When true, all Logitech scans, USB enumeration, and HID++ traffic are skipped.</summary>
    public static bool LogitechDisabled
    {
        get => AppConfig.Is("peripherals_logitech_disabled");
        set => AppConfig.Set("peripherals_logitech_disabled", value ? 1 : 0);
    }

    /// <summary>When true, all ASUS mouse/keyboard scans and USB enumeration are skipped.</summary>
    public static bool AsusPeripheralsDisabled
    {
        get => AppConfig.Is("peripherals_asus_disabled");
        set => AppConfig.Set("peripherals_asus_disabled", value ? 1 : 0);
    }

    /// <summary>Probes for all known ASUS mouse models. Skips already-connected models.</summary>
    public static void DetectAllAsusMice()
    {
        if (AsusPeripheralsDisabled)
            return;
        DetectMouse(new Mouse.Models.ChakramX());
        DetectMouse(new Mouse.Models.ChakramXWired());
        DetectMouse(new Mouse.Models.GladiusIIIAimpoint());
        DetectMouse(new Mouse.Models.GladiusIIIAimpointWired());
        DetectMouse(new Mouse.Models.GladiusIIOrigin());
        DetectMouse(new Mouse.Models.GladiusIIOriginPink());
        DetectMouse(new Mouse.Models.GladiusIIOriginCOD());
        DetectMouse(new Mouse.Models.GladiusII());
        DetectMouse(new Mouse.Models.GladiusIICore());
        DetectMouse(new Mouse.Models.GladiusIIWireless());
        DetectMouse(new Mouse.Models.KerisWireless());
        DetectMouse(new Mouse.Models.KerisWirelessWired());
        DetectMouse(new Mouse.Models.Keris());
        DetectMouse(new Mouse.Models.KerisWirelessEvaEdition());
        DetectMouse(new Mouse.Models.KerisWirelessEvaEditionWired());
        DetectMouse(new Mouse.Models.TUFM4Air());
        DetectMouse(new Mouse.Models.TUFM4Wirelss());
        DetectMouse(new Mouse.Models.TUFM4WirelssCN());
        DetectMouse(new Mouse.Models.StrixImpactIIWireless());
        DetectMouse(new Mouse.Models.StrixImpactIIWirelessWired());
        DetectMouse(new Mouse.Models.GladiusIIIWireless());
        DetectMouse(new Mouse.Models.GladiusIIIWired());
        DetectMouse(new Mouse.Models.GladiusIII());
        DetectMouse(new Mouse.Models.GladiusIIIAimpointEva2());
        DetectMouse(new Mouse.Models.GladiusIIIAimpointEva2Wired());
        DetectMouse(new Mouse.Models.HarpeAceAimLabEdition());
        DetectMouse(new Mouse.Models.HarpeAceAimLabEditionWired());
        DetectMouse(new Mouse.Models.HarpeAceMiniWired());
        DetectMouse(new Mouse.Models.HarpeAceMiniOmni());
        DetectMouse(new Mouse.Models.HarpeMiniCore());
        DetectMouse(new Mouse.Models.HarpeIIAceWireless());
        DetectMouse(new Mouse.Models.HarpeIIAceWired());
        DetectMouse(new Mouse.Models.TUFM3());
        DetectMouse(new Mouse.Models.TUFM3GenII());
        DetectMouse(new Mouse.Models.TUFM5());
        DetectMouse(new Mouse.Models.KerisWirelssAimpoint());
        DetectMouse(new Mouse.Models.KerisWirelssAimpointWired());
        DetectMouse(new Mouse.Models.KerisIIAceWired());
        DetectMouse(new Mouse.Models.KerisIIOriginWired());
        DetectMouse(new Mouse.Models.KerisIIOriginKJPWired());
        DetectMouse(new Mouse.Models.PugioII());
        DetectMouse(new Mouse.Models.PugioIIWired());
        DetectMouse(new Mouse.Models.StrixImpactII());
        DetectMouse(new Mouse.Models.StrixImpactIIElectroPunk());
        DetectMouse(new Mouse.Models.StrixImpactIIMoonlightWhite());
        DetectMouse(new Mouse.Models.Chakram());
        DetectMouse(new Mouse.Models.ChakramWired());
        DetectMouse(new Mouse.Models.ChakramCore());
        DetectMouse(new Mouse.Models.SpathaX());
        DetectMouse(new Mouse.Models.SpathaXWired());
        DetectMouse(new Mouse.Models.StrixCarry());
        DetectMouse(new Mouse.Models.StrixImpactIII());
        DetectMouse(new Mouse.Models.StrixImpactIIIWirelessOmni());
        DetectMouse(new Mouse.Models.StrixImpact());
        DetectMouse(new Mouse.Models.TXGamingMini());
        DetectMouse(new Mouse.Models.TXGamingMiniWired());
        DetectMouse(new Mouse.Models.TUFGamingMiniMiku());
        DetectMouse(new Mouse.Models.TUFGamingMiniMikuWired());
        DetectMouse(new Mouse.Models.Pugio());
        DetectMouse(new Mouse.Models.MD200());
    }

    private static void DetectMouse(IMousePeripheral mouse)
    {
        if (mouse.IsDeviceConnected())
        {
            lock (_lock)
            {
                if (ConnectedMice.Any(m => m.GetDisplayName() == mouse.GetDisplayName()))
                    return;

                try
                {
                    mouse.Connect();
                    mouse.SynchronizeDevice();
                    if (mouse is Logitech.LogitechMouse logi)
                        logi.ApplySavedSettings();
                    ConnectedMice.Add(mouse);
                    DeviceChanged?.Invoke();
                    Logger.WriteLine($"Peripherals: connected {mouse.GetDisplayName()}");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Peripherals: failed to connect {mouse.GetDisplayName()}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Re-reads battery for all mice. Prunes devices with dead
    /// HID handles and triggers re-detection for reconnect.</summary>
    public static void RefreshBatteryForAllDevices(bool force = false)
    {
        bool pruned = false;
        lock (_lock)
        {
            foreach (var m in ConnectedMice)
            {
                try
                {
                    m.ReadBattery();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Peripherals: battery read failed: {ex.Message}");
                }
            }

            // Prune devices with dead HID handles.
            for (int i = ConnectedMice.Count - 1; i >= 0; i--)
            {
                if (ConnectedMice[i] is LogitechMouse logi && logi.IsDeviceStale)
                {
                    Logger.WriteLine($"Peripherals: {logi.GetDisplayName()} stale, removing for re-detect");
                    try
                    { logi.Disconnect(); }
                    catch { }
                    ConnectedMice.RemoveAt(i);
                    pruned = true;
                }
            }
        }

        if (pruned)
        {
            DeviceChanged?.Invoke();
            // Device may reappear at a different hidraw path.
            Task.Run(() =>
            {
                try
                { DetectAllMice(); }
                catch (Exception ex)
                { Logger.WriteLine($"Peripherals: re-detect after stale prune failed: {ex.Message}"); }
            });
        }
    }

    /// <summary>Streams a single colour to all connected mice (Aura Sync).</summary>
    public static void StreamMouseColor(Color color)
    {
        lock (_lock)
        {
            foreach (var mouse in ConnectedMice)
            {
                try
                {
                    mouse.WriteColorDirect(color);
                }
                catch
                {
                    // Swallow, streaming is best-effort
                }
            }
        }
    }

    /// <summary>Detects Logitech mice connected directly via USB or Bluetooth.</summary>
    public static void DetectLogitechMice()
    {
        if (LogitechDisabled)
            return;
        // G PRO family
        DetectMouse(new GProWired());
        DetectMouse(new GProWireless());
        DetectMouse(new GProXSuperlight());
        DetectMouse(new GProXSuperlight2());

        // G series gaming (current)
        DetectMouse(new G402());
        DetectMouse(new G403());
        DetectMouse(new G502());
        DetectMouse(new G502ProteusSpectrum());
        DetectMouse(new G502Hero());
        DetectMouse(new G502Lightspeed());
        DetectMouse(new G502X());
        DetectMouse(new G502XPlus());
        DetectMouse(new G502XLightspeed());
        DetectMouse(new G604());
        DetectMouse(new G703Lightspeed());
        DetectMouse(new G703());
        DetectMouse(new G900());
        DetectMouse(new G903Lightspeed());
        DetectMouse(new G903Hero());

        // G series legacy (HID++ 1.0, wired USB only)
        DetectMouse(new G9());
        DetectMouse(new G9x());
        DetectMouse(new G500());
        DetectMouse(new G500s());
        DetectMouse(new G700());
        DetectMouse(new G700s());

        // MX series productivity
        DetectMouse(new MXMasterBT());
        DetectMouse(new MXMasterBTv2());
        DetectMouse(new MXMaster2SBT());
        DetectMouse(new MXMaster3BT());
        DetectMouse(new MXMaster3S());
        DetectMouse(new MXMaster3SBT());
        DetectMouse(new MXMaster4BT());
        DetectMouse(new MXAnywhere3BT());
        DetectMouse(new MXAnywhere3S());
        DetectMouse(new MXAnywhere3SBT());
        DetectMouse(new MXVertical());
        DetectMouse(new MXVerticalBT());
        DetectMouse(new MXErgoBT());
        DetectMouse(new MXRevolution());
        DetectMouse(new MX518());
        DetectMouse(new M500S());

        // Keyboards (HID++ devices, mouse-specific UI auto-hides)
        DetectMouse(new CraftKeyboard());
        DetectMouse(new MXKeysKeyboard());
        DetectMouse(new G915TKL());
        DetectMouse(new G213());
        DetectMouse(new G512());
        DetectMouse(new G815());
        DetectMouse(new K845());
        DetectMouse(new ProGamingKeyboard());
        DetectMouse(new IlluminatedKeyboard());

        // Headsets (battery + connection features)
        DetectMouse(new G533Headset());
        DetectMouse(new G535Headset());
        DetectMouse(new G733Headset());
        DetectMouse(new G733HeadsetNew());
        DetectMouse(new G935Headset());
        DetectMouse(new ProXHeadset());
    }

    /// <summary>
    /// Scans for known Logitech wireless receivers and enumerates paired devices.
    /// Devices discovered through receivers are added to ConnectedMice.
    /// </summary>
    public static void DetectLogitechReceivers()
    {
        if (LogitechDisabled)
            return;
        foreach (var entry in ReceiverInfo.KnownReceivers)
        {
            try
            {
                using var receiver = new LogitechReceiver(entry);
                if (!receiver.IsConnected())
                    continue;

                Logger.WriteLine($"Peripherals: found {entry.Name} (0x{entry.ProductId:X4})");

                var mice = receiver.DiscoverDevices();
                foreach (var mouse in mice)
                {
                    lock (_lock)
                    {
                        if (ConnectedMice.Any(m => m.GetDisplayName() == mouse.GetDisplayName()))
                            continue;

                        try
                        {
                            mouse.Connect();
                            mouse.SynchronizeDevice();
                            if (mouse is Logitech.LogitechMouse logi)
                                logi.ApplySavedSettings();
                            ConnectedMice.Add(mouse);
                            DeviceChanged?.Invoke();
                            Logger.WriteLine($"Peripherals: connected {mouse.GetDisplayName()} via receiver");
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteLine($"Peripherals: failed to connect {mouse.GetDisplayName()} via receiver: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Peripherals: receiver scan failed for 0x{entry.ProductId:X4}: {ex.Message}");
            }
        }
    }

    /// <summary>Detects all supported mice (ASUS + Logitech).</summary>
    public static void DetectAllMice()
    {
        DetectAllAsusMice();
        DetectLogitechMice();
        DetectLogitechReceivers();
    }

    /// <summary>Drops disabled-vendor devices and re-detects enabled ones.
    /// Called when the user toggles the disable checkboxes.</summary>
    public static void NotifyConfigChanged()
    {
        lock (_lock)
        {
            for (int i = ConnectedMice.Count - 1; i >= 0; i--)
            {
                var m = ConnectedMice[i];
                bool drop = (LogitechDisabled && m is LogitechMouse)
                         || (AsusPeripheralsDisabled && m is Mouse.AsusMouse);
                if (!drop)
                    continue;

                try
                { m.Disconnect(); }
                catch { }
                ConnectedMice.RemoveAt(i);
            }
        }

        // Fire the UI update first so a vendor switching from on -> off
        // empties the panel without waiting for any rediscovery work.
        DeviceChanged?.Invoke();

        // Re-detect enabled vendors on a background thread.
        if (!LogitechDisabled || !AsusPeripheralsDisabled)
        {
            Task.Run(() =>
            {
                try
                { DetectAllMice(); }
                catch (Exception ex)
                { Logger.WriteLine($"Peripherals: re-detect after config change failed: {ex.Message}"); }
            });
        }
    }

    private static bool _hidEventsRegistered;

    /// <summary>Subscribes to HidSharp device-change events to re-scan on plug/unplug.</summary>
    public static void RegisterForDeviceEvents()
    {
        // Always subscribe once. Handler short-circuits when both vendors off.
        if (_hidEventsRegistered)
            return;

        try
        {
            DeviceList.Local.Changed += (_, _) =>
            {
                if (LogitechDisabled && AsusPeripheralsDisabled)
                    return;
                Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => DetectAllMice());
            };
            _hidEventsRegistered = true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Peripherals: HID event registration failed: {ex.Message}");
        }
    }
}
