using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu;

/// <summary>The /etc files that set the GPU state at the next boot: helper script, else inline pkexec, else the test sandbox.</summary>
internal static class GpuBootFiles
{
    // install/tests/test-gpu-block-helper.sh parses these paths, HelperSearchPaths and the block contents.
    private static string TestPathPrefix => GPUModeControl.TestPathPrefix;

    /// <summary>Path to modprobe.d file that blocks dGPU driver loading (NVIDIA + AMD).</summary>
    internal static readonly string ModprobeBlockPath = TestPathPrefix + "/etc/modprobe.d/ghelper-gpu-block.conf";

    /// <summary>Path to udev rule that removes dGPU PCI devices from the bus (NVIDIA + AMD).</summary>
    internal static readonly string UdevRemovePath = TestPathPrefix + "/etc/udev/rules.d/50-ghelper-remove-dgpu.rules";

    /// <summary>Path to trigger file read by ghelper on startup.</summary>
    internal static readonly string TriggerPath = TestPathPrefix + "/etc/ghelper/pending-gpu-mode";

    /// <summary>
    /// Persistent Eco marker. Unlike TriggerPath (consumed after one boot),
    /// this file survives boot-script cleanup. When present and TriggerPath is
    /// absent, the boot script treats its content as the pending mode, making
    /// Eco survive reboots on firmware that forgets dgpu_disable.
    /// </summary>
    internal static readonly string PersistentTriggerPath = TestPathPrefix + "/etc/ghelper/persistent-gpu-mode";

    /// <summary>Path to backend selector file. Content "asus-wmi" or "pci".</summary>
    internal static readonly string BackendPath = TestPathPrefix + "/etc/ghelper/backend";

    /// <summary>Boot recovery marker; the app shows it once, then clears it.</summary>
    internal static readonly string RecoveryMarkerPath = TestPathPrefix + "/etc/ghelper/last-recovery";

    /// <summary>Our PCI-Eco block artifacts (modprobe blacklist + udev
    /// hot-remove rule). Their presence means a dGPU exists but is hidden.</summary>
    internal static bool EcoBlocksPresent() =>
        File.Exists(ModprobeBlockPath) || File.Exists(UdevRemovePath);

    internal static bool TriggerPresent() => File.Exists(TriggerPath);

    /// <summary>Check whether the persistent Eco marker file exists on disk.</summary>
    internal static bool PersistentPresent()
    {
        try
        { return File.Exists(PersistentTriggerPath); }
        catch { return false; }
    }

    /// <summary>Write the boot files for <paramref name="target"/>: Eco blocks the driver; other modes set the trigger (pci) or clean up (asus-wmi).</summary>
    internal static void Write(GpuMode target, MuxLatch mux)
    {
        try
        {
            bool isPci = AppConfig.IsPciGpuBackend();
            bool isEco = (target == GpuMode.Eco);
            // ebpf keeps the dGPU on the bus: no block files.
            bool writeBlocks = isEco && !AppConfig.IsBpfGpuBackend();
            // ebpf Optimized: the boot service picks the leg by power source.
            bool bpfOptimized = AppConfig.IsBpfGpuBackend() && target == GpuMode.Optimized;

            if (!isEco && !isPci && !bpfOptimized)
            {
                // ASUS WMI backend, non-eco target: dGPU driver should be
                // available immediately, no block artifacts needed.
                Clean();
                return;
            }

            // SAFETY: Never write Eco block artifacts when MUX is latched to 0 (Ultimate).
            // After reboot, MUX=0 means dGPU is the sole display - blocking dGPU driver and
            // writing dgpu_disable=1 would cause a black screen (impossible state).
            // WouldCreateImpossibleState only triggers on ASUS hardware with a real MUX;
            // PCI backend on non-ASUS naturally returns false.
            if (isEco && mux.WouldCreateImpossibleState(target))
            {
                Logger.WriteLine("GPUModeControl: WriteDriverBlock REFUSED - Eco + MUX=0 is impossible state, removing any stale artifacts instead");
                Clean();
                return;
            }

            // Convert target mode to string for trigger file (boot script reads this)
            string modeStr = target switch
            {
                GpuMode.Eco => "eco",
                GpuMode.Standard => "standard",
                GpuMode.Optimized => "optimized",
                GpuMode.Ultimate => "ultimate",
                _ => "eco"
            };

            // Pass the active backend through to the helper so it writes the
            // marker file the boot script reads. Default "asus-wmi" preserves
            // legacy behaviour for any caller that hasn't opted into PCI mode.
            string backend = AppConfig.GetGpuBackend();

            if (writeBlocks)
                Logger.WriteLine($"GPUModeControl: writing driver block (modprobe + udev + trigger=eco, backend={backend})");
            else
                Logger.WriteLine($"GPUModeControl: writing trigger={modeStr} (no block artifacts, backend={backend})");

            if (GPUModeControl.IsTestMode)
            {
                // Bypass privilege escalation entirely under the test harness.
                RunHelperInTestMode("write", target, backend);
            }
            else
            {
                string? helper = FindHelperScript();
                if (helper != null)
                {
                    Logger.WriteLine($"GPUModeControl: using helper: {helper}");
                    SysfsHelper.RunSudoOrPkexec(helper, new[] { "write", modeStr, backend }, sudoTimeoutMs: 120000, pkexecTimeoutMs: 120000);
                }
                else
                {
                    // Fallback: pkexec with inline content. Eco writes the full
                    // block artifact set; non-eco (only reachable in PCI mode
                    // here) writes just the trigger + backend marker and removes
                    // any stale modprobe/udev artifacts.
                    Logger.WriteLine($"GPUModeControl: using pkexec fallback (mode={modeStr}, backend={backend})");
                    string script;
                    if (writeBlocks)
                    {
                        script = $"mkdir -p /etc/ghelper\n" +
                            $"cat > {ModprobeBlockPath} << 'GHELPER_BLOCK'\n{ModprobeBlockContent}GHELPER_BLOCK\n" +
                            $"chmod 644 {ModprobeBlockPath}\n" +
                            $"cat > {UdevRemovePath} << 'GHELPER_BLOCK'\n{UdevRemoveContent}GHELPER_BLOCK\n" +
                            $"chmod 644 {UdevRemovePath}\n" +
                            $"echo {modeStr} > {TriggerPath}\n" +
                            $"echo {backend} > {BackendPath}\n" +
                            $"chmod 644 {BackendPath}";
                    }
                    else
                    {
                        script = $"mkdir -p /etc/ghelper\n" +
                            $"rm -f {ModprobeBlockPath} {UdevRemovePath}\n" +
                            $"echo {modeStr} > {TriggerPath}\n" +
                            $"echo {backend} > {BackendPath}\n" +
                            $"chmod 644 {BackendPath}";
                    }
                    SysfsHelper.RunPkexecBash(script);
                }
            }

            if (File.Exists(TriggerPath))
            {
                Logger.WriteLine($"GPUModeControl: trigger written successfully (mode={modeStr})");
                if (writeBlocks)
                {
                    Logger.WriteLine($"  modprobe: {ModprobeBlockPath}");
                    Logger.WriteLine($"  udev:     {UdevRemovePath}");
                }
                Logger.WriteLine($"  trigger:  {TriggerPath} (content: {modeStr})");
                Logger.WriteLine($"  backend:  {BackendPath} (content: {backend})");
            }
            else
            {
                Logger.WriteLine("GPUModeControl: driver block write failed (pkexec cancelled or error)");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: WriteDriverBlock failed: {ex.Message}");
        }
    }

    /// <summary>Remove the modprobe block, udev rule and trigger; the markers stay. No-op when none exist.</summary>
    internal static void Clean()
    {
        try
        {
            if (!EcoBlocksPresent() && !TriggerPresent())
                return; // Nothing to remove

            Logger.WriteLine("GPUModeControl: removing driver block artifacts (current + legacy)");

            if (GPUModeControl.IsTestMode)
            {
                RunHelperInTestMode("clean");
                return;
            }

            string? helper = FindHelperScript();
            if (helper != null)
            {
                // Helper `clean` removes only the ephemeral Eco artifacts.
                // The backend marker is a persistent user preference and is
                // intentionally preserved here so the next boot still uses
                // the correct backend.
                SysfsHelper.RunSudoOrPkexec(helper, new[] { "clean" }, sudoTimeoutMs: 120000, pkexecTimeoutMs: 120000);
            }
            else
            {
                SysfsHelper.RunCommandWithTimeout("pkexec",
                    $"rm -f {ModprobeBlockPath} {UdevRemovePath} {TriggerPath}", 120000);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: RemoveDriverBlock failed: {ex.Message}");
        }
    }

    /// <summary>Write or remove the persistent Eco marker: the boot script applies Eco on every boot, or once.</summary>
    internal static void SyncPersistent(bool present)
        => SyncPersistent(present ? GpuMode.Eco : GpuMode.Standard);

    /// <summary>Eco and Optimized write their name; any other mode removes the marker.</summary>
    internal static void SyncPersistent(GpuMode mode)
    {
        try
        {
            bool present = mode == GpuMode.Eco || mode == GpuMode.Optimized;
            string modeStr = mode == GpuMode.Optimized ? "optimized" : "eco";
            Logger.WriteLine($"GPUModeControl: sync persistent marker to disk: "
                + (present ? $"persist {modeStr}" : "unpersist"));

            if (GPUModeControl.IsTestMode)
            {
                RunHelperInTestMode(present ? "persist" : "unpersist", mode);
                return;
            }

            string? helper = FindHelperScript();
            if (helper != null)
            {
                var args = present
                    ? new[] { "persist", modeStr }
                    : new[] { "unpersist" };
                SysfsHelper.RunSudoOrPkexec(helper, args, sudoTimeoutMs: 120000, pkexecTimeoutMs: 120000);
            }
            else
            {
                string script = present
                    ? $"mkdir -p /etc/ghelper && echo {modeStr} > {PersistentTriggerPath} && chmod 644 {PersistentTriggerPath}"
                    : $"rm -f {PersistentTriggerPath}";
                SysfsHelper.RunPkexecBash(script);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: SyncPersistentMarkerToDisk({mode}) failed: {ex.Message}");
        }
    }

    /// <summary>The persistent Eco option: the marker on disk and the config flag behind the checkbox.</summary>
    internal static void SetEcoPersistent(bool persistent)
    {
        SyncPersistent(persistent);
        AppConfig.Set("gpu_eco_persistent", persistent ? 1 : 0);

        if (persistent)
            Logger.WriteLine($"GPUModeControl: persistent Eco enabled ({PersistentTriggerPath})");
        else
            Logger.WriteLine("GPUModeControl: persistent Eco disabled");
    }

    /// <summary>Write the backend marker; skipped when it already matches, which saves a polkit prompt. True when the marker holds it.</summary>
    internal static bool SetBackend(string backend)
    {
        try
        {
            if (ReadBackend() == backend)
                return true;

            if (GPUModeControl.IsTestMode)
            {
                RunHelperInTestMode("set-backend", backend: backend);
                return ReadBackend() == backend;
            }

            string? helper = FindHelperScript();
            if (helper != null)
            {
                // Match the WriteDriverBlock invocation style - no nested
                // quoting, no `sh -c`. The helper has a dedicated
                // `set-backend` subcommand that writes only the marker.
                Logger.WriteLine($"GPUModeControl: PushBackendMarker via helper: {backend}");
                SysfsHelper.RunSudoOrPkexec(helper, new[] { "set-backend", backend }, sudoTimeoutMs: 30000, pkexecTimeoutMs: 60000);
            }
            else
            {
                // pkexec fallback uses RunPkexecBash so the script body is
                // passed as a single argument (no quoting hazards).
                Logger.WriteLine($"GPUModeControl: PushBackendMarker via pkexec: {backend}");
                SysfsHelper.RunPkexecBash(
                    $"mkdir -p /etc/ghelper\necho {backend} > {BackendPath}\nchmod 644 {BackendPath}");
            }
            return ReadBackend() == backend;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: PushBackendMarker failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Backend marker content the boot service will read, or "" when missing.</summary>
    internal static string ReadBackend()
    {
        try
        { return File.Exists(BackendPath) ? File.ReadAllText(BackendPath).Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>Drop the boot recovery marker, through the root helper when unlink is denied.</summary>
    internal static bool ClearRecovery()
    {
        try
        {
            if (!File.Exists(RecoveryMarkerPath))
                return true;

            try
            {
                File.Delete(RecoveryMarkerPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GPUModeControl: recovery marker unlink denied ({ex.GetType().Name}), trying helper");
            }

            if (GPUModeControl.IsTestMode)
            {
                RunHelperInTestMode("clear-recovery");
                return !File.Exists(RecoveryMarkerPath);
            }

            string? helper = FindHelperScript();
            if (helper == null)
            {
                Logger.WriteLine("GPUModeControl: no helper script, recovery marker left in place");
                return false;
            }

            // No pkexec: this is startup cleanup, not worth a password prompt.
            SysfsHelper.RunSudoOrPkexec(helper, new[] { "clear-recovery" },
                sudoTimeoutMs: 10000, allowPkexec: false);
            bool gone = !File.Exists(RecoveryMarkerPath);
            Logger.WriteLine($"GPUModeControl: recovery marker clear via helper = {(gone ? "OK" : "FAILED")}");
            return gone;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: ClearRecoveryMarker failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Live PCI Eco to Standard; true when the block files are gone (the helper always exits 0).</summary>
    internal static bool LiveStandard()
    {
        try
        {
            if (GPUModeControl.IsTestMode)
            {
                // Honour the test-harness failure switch so the
                // C# scenario suite can exercise the fall-through path.
                if (Environment.GetEnvironmentVariable("GHELPER_TEST_FAIL_LIVE_STANDARD") == "1")
                {
                    Logger.WriteLine("TryLiveRemovePciBlocks: test harness requested failure");
                    return false;
                }
                RunHelperInTestMode("live-standard");
            }
            else
            {
                string? helper = FindHelperScript();
                if (helper != null)
                {
                    Logger.WriteLine($"GPUModeControl: live Eco→Standard via helper: {helper}");
                    SysfsHelper.RunSudoOrPkexec(helper, new[] { "live-standard" }, sudoTimeoutMs: 30000, pkexecTimeoutMs: 60000);
                }
                else
                {
                    Logger.WriteLine("GPUModeControl: live Eco→Standard via pkexec fallback");
                    // RunPkexecBash passes the script body as a single
                    // ArgumentList entry, so the interpolated path
                    // constants below cannot escape into argv parsing.
                    // The paths themselves are private const strings -
                    // not user input.
                    string script =
                        $"rm -f {ModprobeBlockPath} {UdevRemovePath} {TriggerPath}\n" +
                        "udevadm control --reload-rules 2>/dev/null || true\n" +
                        "echo 1 > /sys/bus/pci/rescan 2>/dev/null || true\n" +
                        "modprobe nvidia 2>/dev/null || true\n" +
                        "modprobe amdgpu 2>/dev/null || true";
                    SysfsHelper.RunPkexecBash(script);
                }
            }

            // Verify the recovery actually happened. The helper script
            // returns 0 even when individual operations fail (the trailing
            // `|| true`); the only reliable signal is file-state.
            if (EcoBlocksPresent())
                return false;
            // PCI Eco hid the NVIDIA Vulkan and EGL files; the driver is back.
            if (!GPUModeControl.IsTestMode)
                DgpuDriver.ApplyVulkanIcd(dgpuAvailable: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"TryLiveRemovePciBlocks failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// In-process replacement for the sudo/pkexec helper calls when running
    /// under the C# test harness. Performs the exact same file-system effect
    /// the helper script would, but without any privilege escalation.
    /// Mirrors gpu-block-helper.sh write/clean/set-backend semantics.
    /// </summary>
    private static void RunHelperInTestMode(string action, GpuMode? target = null, string? backend = null)
    {
        try
        {
            string? mkdir(string p)
            { var d = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d); return d; }
            switch (action)
            {
                case "write":
                    string modeStr = (target ?? GpuMode.Eco) switch
                    {
                        GpuMode.Eco => "eco",
                        GpuMode.Standard => "standard",
                        GpuMode.Optimized => "optimized",
                        GpuMode.Ultimate => "ultimate",
                        _ => "eco",
                    };
                    string be = backend ?? "asus-wmi";
                    mkdir(BackendPath);
                    File.WriteAllText(BackendPath, be);
                    // Mirrors the bash helper: no block files for ebpf.
                    if (modeStr == "eco" && be != "ebpf")
                    {
                        mkdir(ModprobeBlockPath);
                        File.WriteAllText(ModprobeBlockPath, ModprobeBlockContent);
                        mkdir(UdevRemovePath);
                        File.WriteAllText(UdevRemovePath, UdevRemoveContent);
                    }
                    else
                    {
                        if (File.Exists(ModprobeBlockPath))
                            File.Delete(ModprobeBlockPath);
                        if (File.Exists(UdevRemovePath))
                            File.Delete(UdevRemovePath);
                    }
                    mkdir(TriggerPath);
                    File.WriteAllText(TriggerPath, modeStr);
                    break;
                case "clean":
                    if (File.Exists(ModprobeBlockPath))
                        File.Delete(ModprobeBlockPath);
                    if (File.Exists(UdevRemovePath))
                        File.Delete(UdevRemovePath);
                    if (File.Exists(TriggerPath))
                        File.Delete(TriggerPath);
                    break;
                case "set-backend":
                    mkdir(BackendPath);
                    File.WriteAllText(BackendPath, backend ?? "asus-wmi");
                    break;
                case "clear-recovery":
                    if (File.Exists(RecoveryMarkerPath))
                        File.Delete(RecoveryMarkerPath);
                    break;
                case "persist":
                    mkdir(PersistentTriggerPath);
                    File.WriteAllText(PersistentTriggerPath,
                        target == GpuMode.Optimized ? "optimized" : "eco");
                    break;
                case "unpersist":
                    if (File.Exists(PersistentTriggerPath))
                        File.Delete(PersistentTriggerPath);
                    break;
                case "live-standard":
                    // Mirror the bash helper: remove blocks + trigger + persistent marker.
                    // udevadm + PCI rescan + modprobe are out-of-scope
                    // for a userland test sandbox (no real kernel).
                    if (File.Exists(ModprobeBlockPath))
                        File.Delete(ModprobeBlockPath);
                    if (File.Exists(UdevRemovePath))
                        File.Delete(UdevRemovePath);
                    if (File.Exists(TriggerPath))
                        File.Delete(TriggerPath);
                    if (File.Exists(PersistentTriggerPath))
                        File.Delete(PersistentTriggerPath);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"RunHelperInTestMode({action}) failed: {ex.Message}");
        }
    }

    /// <summary>Content for the modprobe.d block file (vendor-aware: NVIDIA + AMD).</summary>
    private const string ModprobeBlockContent =
        "# ghelper: block dGPU driver modules so dGPU can be safely disabled on next boot\n" +
        "# Auto-generated - will be removed after Eco mode is applied\n" +
        "# Uses 'install /bin/false' (strongest block - prevents loading by ANY means)\n" +
        "# NVIDIA modules\n" +
        "install nvidia /bin/false\n" +
        "install nvidia_drm /bin/false\n" +
        "install nvidia_modeset /bin/false\n" +
        "install nvidia_uvm /bin/false\n" +
        "install nvidia_wmi_ec_backlight /bin/false\n" +
        "# Open-source NVIDIA driver\n" +
        "install nouveau /bin/false\n" +
        "# AMD dGPU driver\n" +
        "install amdgpu /bin/false\n";

    /// <summary>Content for the udev rule that PCI-removes dGPU devices (NVIDIA + AMD) on add.</summary>
    private const string UdevRemoveContent =
        "# ghelper: remove dGPU PCI devices so no driver can bind\n" +
        "# Auto-generated - will be removed after Eco mode is applied\n" +
        "# Remove NVIDIA VGA controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x030000\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA 3D controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x030200\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA Audio devices (HDMI audio on dGPU)\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x040300\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA USB xHCI Host Controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x0c0330\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove NVIDIA USB Type-C UCSI devices\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x10de\", ATTR{class}==\"0x0c8000\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU VGA controller (boot_vga!=1 protects the iGPU)\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x030000\", ATTR{boot_vga}!=\"1\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU 3D controller\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x030200\", ATTR{boot_vga}!=\"1\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n" +
        "# Remove AMD dGPU Audio devices\n" +
        "ACTION==\"add\", SUBSYSTEM==\"pci\", ATTR{vendor}==\"0x1002\", ATTR{class}==\"0x040300\", ATTR{power/control}=\"auto\", ATTR{remove}=\"1\"\n";

    /// <summary>Known locations for the GPU block helper script (installed by install-local.sh).</summary>
    private static readonly string[] HelperSearchPaths = new[]
    {
        "/usr/local/lib/ghelper/gpu-block-helper.sh",
        "/opt/ghelper/gpu-block-helper.sh",
    };

    /// <summary>Cached path to the helper script. Null if not found.</summary>
    private static string? _cachedHelperPath;
    private static bool _helperPathScanned;

    /// <summary>
    /// Find the GPU block helper script. Checked once and cached.
    /// Returns null if not found (falls back to pkexec).
    /// </summary>
    private static string? FindHelperScript()
    {
        if (_helperPathScanned)
            return _cachedHelperPath;
        _helperPathScanned = true;

        // NixOS: module puts gpu-block-helper.sh on PATH
        var nixPath = NixOS.ResolveGpuBlockHelper();
        if (nixPath != null)
        {
            _cachedHelperPath = nixPath;
            Logger.WriteLine($"GPUModeControl: GPU block helper found at {nixPath} (NixOS)");
            return nixPath;
        }

        foreach (var path in HelperSearchPaths)
        {
            if (File.Exists(path))
            {
                _cachedHelperPath = path;
                Logger.WriteLine($"GPUModeControl: GPU block helper found at {path}");
                return path;
            }
        }

        Logger.WriteLine("GPUModeControl: GPU block helper not found - will use pkexec fallback");
        return null;
    }
}
