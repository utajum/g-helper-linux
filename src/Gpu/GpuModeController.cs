using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Gpu;

/// <summary>
/// GPU mode: the 4 user-visible modes in the UI.
/// </summary>
public enum GpuMode
{
    Eco = 0,
    Standard = 1,
    Optimized = 2,
    Ultimate = 3
}

/// <summary>
/// Result of a GPU mode switch attempt. The UI uses this to decide
/// what notification/dialog/tip to show.
/// </summary>
public enum GpuSwitchResult
{
    /// <summary>Mode applied immediately — hardware state changed.</summary>
    Applied,
    /// <summary>Hardware already in the desired state — no write needed.</summary>
    AlreadySet,
    /// <summary>MUX change latched — reboot required to take effect.</summary>
    RebootRequired,
    /// <summary>dGPU driver is active — cannot safely write dgpu_disable=1.
    /// UI should show confirmation dialog (Switch Now / After Reboot / Cancel).</summary>
    DriverBlocking,
    /// <summary>Mode saved to config for next reboot (user chose "After Reboot").</summary>
    Deferred,
    /// <summary>Write failed (sysfs error, permission denied, etc.).</summary>
    Failed,
    /// <summary>Eco mode blocked — MUX was set to 0 (Ultimate) this boot session. Reboot first.</summary>
    EcoBlocked
}

/// <summary>
/// Centralized GPU mode switching controller.
///
/// Every danger in this system comes from a single operation: writing dgpu_disable=1
/// while the dGPU driver is loaded and active. Everything else is either instant or
/// just requires a reboot. The entire safety architecture exists to protect that one write.
///
/// Architecture: button handlers and tray menu call this controller. They never write
/// sysfs directly. The controller reads current hardware state, computes the delta,
/// and executes the needed operations with safety checks.
///
/// See GPU_MODE_PLAN.md for the complete scenario matrix and flows.
/// </summary>
public class GpuModeController
{
    private readonly IAsusWmi _wmi;
    private readonly IPowerManager _power;

    /// <summary>Lock to prevent concurrent GPU mode operations.
    /// Writing dgpu_disable can block for 30-60 seconds — we must not queue multiple writes.
    /// SemaphoreSlim(1,1) provides atomic check-and-acquire, eliminating the TOCTOU race
    /// that existed with the previous volatile bool approach.</summary>
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    /// <summary>
    /// Tracks pending MUX latch value within this session.
    /// After SetGpuMuxMode(x), hardware still reports the OLD value until reboot.
    /// This field remembers what we latched so ComputeAndExecute() and ScheduleModeForReboot()
    /// use the correct effective MUX, not the stale hardware readback.
    /// -1 = no pending latch (use hardware value).
    /// </summary>
    private volatile int _pendingMuxLatch = -1;

    /// <summary>Cached dGPU PCI address for AMD systems (e.g., "0000:01:00.0").</summary>
    private string? _cachedDgpuPciAddress;
    private bool _dgpuPciScanned;

    public GpuModeController(IAsusWmi wmi, IPowerManager power)
    {
        _wmi = wmi;
        _power = power;
    }

    /// <summary>True if a GPU mode switch is currently in progress (sysfs write blocking).</summary>
    public bool IsSwitchInProgress => _switchLock.CurrentCount == 0;

    /// <summary>
    /// Return the effective MUX value: if we latched a change this session, return
    /// the latched value. Otherwise return actual hardware state.
    /// Used by ComputeAndExecute(), ExecuteDisableDgpu(), ScheduleModeForReboot() —
    /// methods that need to know "what MUX will be after reboot".
    /// NOT used by GetCurrentMode(), AutoGpuSwitch(), IsPendingReboot(),
    /// ApplyPendingOnStartup(), ApplyPendingOnShutdown() — those need actual hardware.
    /// </summary>
    private int GetEffectiveMux()
    {
        int pending = _pendingMuxLatch;
        return pending >= 0 ? pending : _wmi.GetGpuMuxMode();
    }

    /// <summary>
    /// Check if MUX=0 was written at any point during the current boot session.
    /// Uses persistent storage (AppConfig) keyed by boot_id, so it survives app restarts
    /// within the same boot session but auto-clears after reboot.
    /// </summary>
    private static bool IsMuxZeroLatchedThisBoot()
    {
        try
        {
            string? stored = AppConfig.GetString("mux_zero_latched_boot_id");
            if (string.IsNullOrEmpty(stored)) return false;
            string current = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            return stored == current;
        }
        catch (IOException)
        {
            // /proc/sys/kernel/random/boot_id not readable (container/chroot) — fail-open
            return false;
        }
    }

    /// <summary>
    /// Persist the MUX=0 latch flag for the current boot session.
    /// Called after every successful SetGpuMuxMode(0) write.
    /// The flag stays set until reboot (boot_id changes).
    /// </summary>
    private static void SetMuxZeroLatchFlag()
    {
        try
        {
            string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            AppConfig.Set("mux_zero_latched_boot_id", bootId);
            Logger.WriteLine($"GpuModeController: MUX=0 latch persisted — boot_id={bootId}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: failed to persist MUX=0 latch flag: {ex.Message}");
        }
    }

    /// <summary>
    /// SAFETY: Would the given target mode create an impossible post-reboot state?
    /// The ONE impossible state: MUX=0 (dGPU sole display) + dgpu_disable=1 (dGPU off)
    /// = guaranteed black screen.
    ///
    /// Defense in depth: checks BOTH the persistent boot_id flag (survives app restart)
    /// AND the in-memory effective MUX latch (fast path, same session).
    /// Either check returning true blocks Eco.
    /// </summary>
    private bool WouldCreateImpossibleState(GpuMode target)
    {
        if (target != GpuMode.Eco) return false;

        // Check persistent flag first (survives app restart within same boot)
        if (IsMuxZeroLatchedThisBoot())
        {
            Logger.WriteLine("GpuModeController: IMPOSSIBLE STATE PREVENTED — MUX=0 was written this boot session (persistent flag). Eco + MUX=0 = black screen.");
            return true;
        }

        // Also check in-memory latch (fast path, same session — defense in depth)
        int effectiveMux = GetEffectiveMux();
        if (effectiveMux == 0)
        {
            Logger.WriteLine("GpuModeController: IMPOSSIBLE STATE PREVENTED — cannot schedule Eco when MUX is latched to 0 (Ultimate). Eco + MUX=0 = black screen.");
            return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The single entry point for all GPU mode switches (buttons + tray menu).
    /// Determines current hardware state, computes needed operations, executes
    /// with safety checks. Returns result for UI to act on.
    ///
    /// NOTE: This may block for 30-60 seconds (dgpu_disable write).
    /// Always call from a background thread (Task.Run), never from the UI thread.
    /// </summary>
    public GpuSwitchResult RequestModeSwitch(GpuMode target)
    {
        if (!_switchLock.Wait(0))
        {
            // A hardware switch is blocking — can't start another.
            // But save the user's latest choice so it applies after reboot.
            // This ensures rapid clicks always result in the LAST choice winning.
            Logger.WriteLine($"GpuModeController: switch in progress, scheduling {target} for reboot");
            var scheduleResult = ScheduleModeForReboot(target);
            if (scheduleResult == GpuSwitchResult.EcoBlocked)
                return GpuSwitchResult.EcoBlocked;
            return GpuSwitchResult.Deferred;
        }

        try
        {
            Logger.WriteLine($"GpuModeController: RequestModeSwitch → {target}");
            return ComputeAndExecute(target);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: RequestModeSwitch({target}) failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>
    /// Attempt to release the GPU driver (pkexec rmmod / PCI unbind) and then
    /// write dgpu_disable=1. Called from the "Switch Now" confirmation dialog button.
    ///
    /// NOTE: This may show a polkit password dialog and block for a while.
    /// Always call from a background thread.
    /// </summary>
    public GpuSwitchResult TryReleaseAndSwitch()
    {
        if (!_switchLock.Wait(0))
            return GpuSwitchResult.AlreadySet;

        try
        {
            Logger.WriteLine("GpuModeController: TryReleaseAndSwitch — attempting driver release");

            bool released = TryReleaseGpuDriver();
            if (!released)
            {
                Logger.WriteLine("GpuModeController: driver release failed — deferring to reboot");
                SaveModeToConfig(GpuMode.Eco);
                // If we're in Ultimate (MUX=0), latch MUX→1 first so next boot can apply Eco
                int effectiveMux = GetEffectiveMux();
                if (effectiveMux == 0)
                {
                    // gpu_mux_mode write fails when dgpu_disable=1 — enable dGPU first if needed
                    bool ecoActive = _wmi.GetGpuEco();
                    if (ecoActive)
                    {
                        Logger.WriteLine("GpuModeController: TryRelease — enabling dGPU before MUX latch");
                        try { _wmi.SetGpuEco(false); RemoveDriverBlock(); }
                        catch (Exception muxEx)
                        {
                            Logger.WriteLine($"GpuModeController: TryRelease — exit Eco failed: {muxEx.Message}");
                        }
                    }
                    Logger.WriteLine("GpuModeController: MUX=0, latching MUX→1 for Eco boot");
                    try
                    {
                        _wmi.SetGpuMuxMode(1);
                        _pendingMuxLatch = 1;
                    }
                    catch (Exception muxEx)
                    {
                        Logger.WriteLine($"GpuModeController: TryRelease — MUX latch failed: {muxEx.Message}");
                    }
                }
                // pkexec auth is cached from rmmod attempt — write block without re-prompting
                WriteDriverBlock(GpuMode.Eco);
                return GpuSwitchResult.Deferred;
            }

            // Driver released — now write dgpu_disable=1 (should be fast)
            Logger.WriteLine("GpuModeController: driver released, writing dgpu_disable=1");
            _wmi.SetGpuEco(true);

            // Verify
            if (_wmi.GetGpuEco())
            {
                SaveModeToConfig(GpuMode.Eco);
                // Eco applied live — remove block artifacts (dgpu_disable=1 is persistent)
                RemoveDriverBlock();
                Logger.WriteLine("GpuModeController: Eco mode applied after driver release");
                return GpuSwitchResult.Applied;
            }
            else
            {
                Logger.WriteLine("GpuModeController: dgpu_disable write succeeded but readback != 1");
                SaveModeToConfig(GpuMode.Eco);
                return GpuSwitchResult.Deferred;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: TryReleaseAndSwitch failed: {ex.Message}");
            SaveModeToConfig(GpuMode.Eco);
            return GpuSwitchResult.Deferred;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>
    /// Save desired mode to config for next reboot. Latches any MUX changes.
    /// Called from the "After Reboot" confirmation dialog button.
    /// Does NOT write dgpu_disable.
    /// Returns the result so callers can distinguish EcoBlocked from RebootRequired.
    /// </summary>
    public GpuSwitchResult ScheduleModeForReboot(GpuMode target)
    {
        Logger.WriteLine($"GpuModeController: ScheduleModeForReboot({target})");

        // SAFETY: If scheduling Eco but MUX is latched to 0, the user changed from
        // Ultimate to Eco without rebooting. After reboot MUX=0 + dgpu_disable=1 = black screen.
        // Refuse: keep config as-is, remove any stale Eco artifacts.
        if (WouldCreateImpossibleState(target))
        {
            Logger.WriteLine("GpuModeController: ScheduleModeForReboot REFUSED — would create impossible post-reboot state (Eco + MUX=0)");
            Logger.WriteLine("GpuModeController: user must reboot into Ultimate first, THEN switch to Eco");
            RemoveDriverBlock();
            return GpuSwitchResult.EcoBlocked;
        }

        SaveModeToConfig(target);

        // If target needs MUX change, latch it now (instant, safe)
        // Use GetEffectiveMux() — if we already latched a MUX change this session,
        // we need to know the LATCHED value, not the stale hardware readback.
        int effectiveMux = GetEffectiveMux();
        int targetMux = (target == GpuMode.Ultimate) ? 0 : 1;

        if (effectiveMux >= 0 && effectiveMux != targetMux)
        {
            // gpu_mux_mode write fails when dgpu_disable=1 — enable dGPU first
            bool ecoEnabled = _wmi.GetGpuEco();
            if (ecoEnabled)
            {
                Logger.WriteLine("GpuModeController: ScheduleModeForReboot — enabling dGPU before MUX latch");
                try
                {
                    _wmi.SetGpuEco(false);
                    RemoveDriverBlock();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GpuModeController: ScheduleModeForReboot — failed to exit Eco: {ex.Message}");
                    // Can't latch MUX, but config is saved — ApplyPendingOnStartup will retry
                    WriteDriverBlock(target);
                    return GpuSwitchResult.RebootRequired;
                }
            }

            Logger.WriteLine($"GpuModeController: latching MUX {effectiveMux} → {targetMux}");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
                _pendingMuxLatch = targetMux;
                if (targetMux == 0)
                    SetMuxZeroLatchFlag();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: ScheduleModeForReboot — MUX write failed: {ex.Message}");
                // Config is saved — ApplyPendingOnStartup will retry
            }
        }

        // Write driver block so Eco can be applied safely after reboot.
        // For non-Eco targets, this removes any stale block artifacts.
        WriteDriverBlock(target);
        return GpuSwitchResult.RebootRequired;
    }

    /// <summary>
    /// On startup: check if config has a pending GPU mode that differs from
    /// current hardware state. If so, try to apply it.
    ///
    /// NOTE: This may block. Call from a background thread.
    /// </summary>
    public GpuSwitchResult ApplyPendingOnStartup()
    {
        // ── Boot safety check (supergfxctl pattern) ──
        // If MUX=0 (Ultimate/dGPU-direct) AND dgpu_disable=1, that's an impossible
        // state that causes boot hangs. Force dgpu_disable=0 to recover.
        // This shouldn't happen with the modprobe.d approach but could occur from
        // manual sysfs tinkering or stale tmpfiles from a previous version.
        BootSafetyCheck();

        // ── Clear MUX=0 latch on reboot detection ──
        // If the stored boot_id differs from current, a reboot happened — safe to clear.
        try
        {
            string? storedBootId = AppConfig.GetString("mux_zero_latched_boot_id");
            if (!string.IsNullOrEmpty(storedBootId))
            {
                string currentBootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                if (storedBootId != currentBootId)
                {
                    AppConfig.Set("mux_zero_latched_boot_id", "");
                    Logger.WriteLine("GpuModeController: reboot detected — cleared MUX=0 latch flag");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: MUX=0 latch reboot check failed: {ex.Message}");
        }

        string? savedMode = AppConfig.GetString("gpu_mode");
        if (string.IsNullOrEmpty(savedMode))
        {
            // No pending mode — clean up any stale block artifacts (crash, uninstall, etc.)
            RemoveDriverBlock();
            return GpuSwitchResult.AlreadySet;
        }

        GpuMode target = ParseGpuMode(savedMode);
        GpuMode current = GetCurrentMode();

        // Check if hardware matches desired mode
        bool ecoEnabled = _wmi.GetGpuEco();
        int mux = _wmi.GetGpuMuxMode();

        bool needsDgpuChange = false;
        bool needsMuxChange = false;

        if (target == GpuMode.Eco && !ecoEnabled)
        {
            if (mux == 0)
            {
                // MUX=0 (Ultimate) — kernel refuses dgpu_disable=1 in this mode.
                // Latch MUX=1 first, then Eco will apply on the NEXT reboot.
                needsMuxChange = true;
            }
            else
            {
                needsDgpuChange = true;
            }
        }
        else if (target == GpuMode.Ultimate && mux != 0)
            needsMuxChange = true;
        else if (target == GpuMode.Standard && mux == 0)
            needsMuxChange = true;
        else if (target == GpuMode.Optimized)
        {
            // Optimized needs MUX=1 first
            if (mux == 0) needsMuxChange = true;
            // Optimized with hardware in Eco — need to enable dGPU
            else if (ecoEnabled) needsDgpuChange = true;
        }
        else if ((target == GpuMode.Standard) && ecoEnabled)
        {
            // Config says Standard but hardware is Eco (rapid-click override scenario).
            // Need to enable dGPU (dgpu_disable=0).
            needsDgpuChange = true;
        }

        if (!needsDgpuChange && !needsMuxChange)
        {
            Logger.WriteLine($"GpuModeController: startup — hardware matches saved mode '{savedMode}'");
            // Hardware matches — clean up block artifacts if they exist (mode was applied)
            RemoveDriverBlock();
            return GpuSwitchResult.AlreadySet;
        }

        if (needsMuxChange)
        {
            // If in Eco, must enable dGPU before MUX change
            // (firmware rejects gpu_mux_mode write when dgpu_disable=1)
            if (ecoEnabled)
            {
                Logger.WriteLine("GpuModeController: startup — enabling dGPU before MUX change");
                try
                {
                    _wmi.SetGpuEco(false);
                    RemoveDriverBlock();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"GpuModeController: startup — failed to exit Eco: {ex.Message}");
                    return GpuSwitchResult.Failed;
                }
            }

            int targetMux = (target == GpuMode.Ultimate) ? 0 : 1;
            Logger.WriteLine($"GpuModeController: startup — latching MUX → {targetMux} for '{savedMode}'");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: startup — MUX write failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            return GpuSwitchResult.RebootRequired;
        }

        // needsDgpuChange — two directions:
        // (a) Target is Eco but hardware is not Eco → disable dGPU
        // (b) Target is Standard/Optimized but hardware is Eco → enable dGPU
        bool targetEco = (target == GpuMode.Eco);

        if (targetEco)
        {
            // ── Direction (a): Apply pending Eco ──
            Logger.WriteLine("GpuModeController: startup — applying pending Eco mode");

            if (IsDgpuDriverActive())
            {
                Logger.WriteLine("GpuModeController: startup — dGPU driver active, cannot apply Eco");
                // MUX is correct (1) but dGPU driver is loaded — write block so NEXT boot
                // driver won't load, then ghelper can write dgpu_disable=1 safely.
                // This breaks the infinite loop: startup → driver active → can't apply → repeat.
                WriteDriverBlock(GpuMode.Eco);
                return GpuSwitchResult.DriverBlocking;
            }

            // Driver not active — safe to write
            if (!_switchLock.Wait(0))
            {
                Logger.WriteLine("GpuModeController: startup — switch lock contention, skipping");
                return GpuSwitchResult.Failed;
            }
            try
            {
                _wmi.SetGpuEco(true);

                if (_wmi.GetGpuEco())
                {
                    Logger.WriteLine("GpuModeController: startup — Eco mode applied successfully");
                    // Eco confirmed — remove block artifacts (dgpu_disable=1 is persistent)
                    RemoveDriverBlock();
                    return GpuSwitchResult.Applied;
                }
                else
                {
                    Logger.WriteLine("GpuModeController: startup — dgpu_disable write failed readback");
                    return GpuSwitchResult.Failed;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: startup apply failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
        else
        {
            // ── Direction (b): Enable dGPU for pending Standard/Optimized ──
            // Hardware is in Eco (dgpu_disable=1) but config says Standard/Optimized.
            // This happens when rapid clicks override a blocking Eco switch.
            Logger.WriteLine($"GpuModeController: startup — enabling dGPU for pending {target} (hardware is Eco)");

            if (!_switchLock.Wait(0))
            {
                Logger.WriteLine("GpuModeController: startup — switch lock contention, skipping");
                return GpuSwitchResult.Failed;
            }
            try
            {
                _wmi.SetGpuEco(false); // Always safe — enables dGPU
                RemoveDriverBlock();    // Clean up stale block artifacts
                Logger.WriteLine($"GpuModeController: startup — dGPU enabled for {target}");
                return GpuSwitchResult.Applied;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: startup enable dGPU failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
    }

    /// <summary>
    /// On shutdown (SIGTERM/SIGINT): best-effort dgpu_disable=1 if Eco is pending.
    /// Still checks driver safety — SIGINT is not a system shutdown (Xorg is still running),
    /// and even during SIGTERM the display stack may not have released the GPU yet.
    /// If driver is active, skip — ApplyPendingOnStartup() will try on next boot.
    /// </summary>
    public void ApplyPendingOnShutdown()
    {
        try
        {
            string? savedMode = AppConfig.GetString("gpu_mode");
            if (savedMode != "eco") return;

            bool ecoEnabled = _wmi.GetGpuEco();
            if (ecoEnabled) return; // Already in Eco

            // MUX=0 guard — kernel refuses dgpu_disable=1 when in Ultimate mode
            int mux = _wmi.GetGpuMuxMode();
            if (mux == 0)
            {
                Logger.WriteLine("GpuModeController: shutdown — MUX=0 (Ultimate), cannot write dgpu_disable");
                Logger.WriteLine("GpuModeController: Eco mode requires MUX=1 first — will handle on next startup");
                return;
            }

            // Safety check — same as everywhere else.
            // Writing dgpu_disable=1 while the driver is active triggers ACPI PCI hot-removal
            // which causes a kernel panic (NULL deref in nvidia_modeset when Xorg still has the GPU).
            if (IsDgpuDriverActive())
            {
                Logger.WriteLine("GpuModeController: shutdown — dGPU driver still active, skipping dgpu_disable write");
                Logger.WriteLine("GpuModeController: Eco mode will be applied on next startup instead");
                return;
            }

            Logger.WriteLine("GpuModeController: shutdown — driver idle, writing dgpu_disable=1 for pending Eco");
            _wmi.SetGpuEco(true);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: shutdown apply failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Optimized mode auto Eco/Standard switch on power state change.
    /// Same safety as RequestModeSwitch but never shows a dialog — returns
    /// DriverBlocking for caller to show a notification instead.
    ///
    /// NOTE: May block for 30-60 seconds. Call from background thread.
    /// </summary>
    public GpuSwitchResult AutoGpuSwitch()
    {
        if (!AppConfig.Is("gpu_auto"))
            return GpuSwitchResult.AlreadySet;

        // Don't auto-switch if in Ultimate (MUX=0)
        int mux = _wmi.GetGpuMuxMode();
        if (mux == 0)
        {
            Logger.WriteLine("GpuModeController: AutoGpuSwitch — MUX=0 (Ultimate), skipping");
            return GpuSwitchResult.AlreadySet;
        }

        // Don't auto-switch to Eco if MUX=0 was latched this boot (persistent flag)
        // Hardware may still read MUX=1, but firmware has MUX=0 pending — Eco would be impossible
        if (IsMuxZeroLatchedThisBoot())
        {
            Logger.WriteLine("GpuModeController: AutoGpuSwitch — MUX=0 latched this boot, Eco path blocked — staying in Standard");
            return GpuSwitchResult.AlreadySet;
        }

        bool onAc = _power.IsOnAcPower();
        bool ecoEnabled = _wmi.GetGpuEco();

        if (onAc && ecoEnabled)
        {
            // Plugged in → enable dGPU (always safe)
            Logger.WriteLine("GpuModeController: AutoGpuSwitch — AC power, enabling dGPU");
            if (!_switchLock.Wait(0)) return GpuSwitchResult.AlreadySet;

            try
            {
                _wmi.SetGpuEco(false);
                // Switching away from Eco — clean up block artifacts
                RemoveDriverBlock();
                Logger.WriteLine("GpuModeController: AutoGpuSwitch — dGPU enabled");
                return GpuSwitchResult.Applied;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: AutoGpuSwitch Eco→Standard failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }
        else if (!onAc && !ecoEnabled)
        {
            // On battery → disable dGPU (THE dangerous path)
            Logger.WriteLine("GpuModeController: AutoGpuSwitch — battery, attempting Eco");
            if (!_switchLock.Wait(0)) return GpuSwitchResult.AlreadySet;

            try
            {
                return ExecuteDisableDgpu();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: AutoGpuSwitch battery→Eco failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            finally
            {
                _switchLock.Release();
            }
        }

        return GpuSwitchResult.AlreadySet;
    }

    /// <summary>
    /// Read current hardware state + config → return the current mode enum.
    /// </summary>
    public GpuMode GetCurrentMode()
    {
        bool gpuAuto = AppConfig.Is("gpu_auto");
        bool ecoEnabled = _wmi.GetGpuEco();
        int mux = _wmi.GetGpuMuxMode();

        if (mux == 0) return GpuMode.Ultimate;
        if (gpuAuto) return GpuMode.Optimized;
        if (ecoEnabled) return GpuMode.Eco;
        return GpuMode.Standard;
    }

    /// <summary>
    /// True if config gpu_mode differs from current hardware state
    /// (mode is waiting for a reboot to take effect).
    /// </summary>
    public bool IsPendingReboot()
    {
        string? saved = AppConfig.GetString("gpu_mode");
        if (string.IsNullOrEmpty(saved)) return false;

        GpuMode target = ParseGpuMode(saved);
        GpuMode current = GetCurrentMode();

        // Simple check: if they differ, something is pending
        if (target == current) return false;

        // More precise: check if the difference requires a reboot
        int mux = _wmi.GetGpuMuxMode();
        bool eco = _wmi.GetGpuEco();

        return target switch
        {
            GpuMode.Eco => !eco,        // Eco pending but not applied
            GpuMode.Ultimate => mux != 0, // MUX change pending
            GpuMode.Standard => mux == 0, // Coming from Ultimate, MUX pending
            GpuMode.Optimized => mux == 0, // Coming from Ultimate, MUX pending
            _ => false
        };
    }

    // ════════════════════════════════════════════════════════════════
    //  Core logic
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Core logic: read current hardware, compute delta, route to Execute* methods.
    /// </summary>
    private GpuSwitchResult ComputeAndExecute(GpuMode target)
    {
        // ── 4×4 Transition Matrix ──
        // From\To     | Eco         | Standard    | Optimized   | Ultimate
        // ------------|-------------|-------------|-------------|-------------
        // Eco         | AlreadySet  | Applied     | Applied*    | RebootReq
        // Standard    | DANGER†     | AlreadySet  | Applied*/†  | RebootReq
        // Optimized   | (delegates) | (delegates) | AlreadySet  | RebootReq‡
        // Ultimate    | Multi-boot§ | RebootReq   | RebootReq   | AlreadySet
        //
        // * Optimized target: targetEco depends on AC power (battery=Eco hw, AC=Standard hw)
        // † DANGER: dgpu_disable=1 when driver active → DriverBlocking dialog
        // ‡ If Optimized hw=Eco, exits Eco first (200ms verify), then MUX write
        // § Ultimate→Eco: MUX latch + DriverBlocking or deferred Eco (2-boot path)
        //
        // Config is saved only on success paths (Applied, RebootRequired, AlreadySet,
        // DriverBlocking). NEVER saved on Failed — prevents stale config from rejected writes.

        // Read current hardware state
        bool currentEco = _wmi.GetGpuEco();     // true if dgpu_disable=1
        // Use effective MUX (accounts for pending latch from earlier this session)
        int currentMux = GetEffectiveMux();       // 0=Ultimate, 1=hybrid

        // Compute target hardware state
        bool targetEco;
        int targetMux;
        bool targetAuto = false;

        switch (target)
        {
            case GpuMode.Eco:
                targetEco = true;
                targetMux = 1;
                break;
            case GpuMode.Standard:
                targetEco = false;
                targetMux = 1;
                break;
            case GpuMode.Optimized:
                targetAuto = true;
                targetMux = 1;
                // On AC: want dGPU on (Standard hw). On battery: want dGPU off (Eco hw).
                targetEco = !_power.IsOnAcPower();
                break;
            case GpuMode.Ultimate:
                targetEco = false;
                targetMux = 0;
                break;
            default:
                return GpuSwitchResult.Failed;
        }

        // gpu_auto is a software flag (no hardware write) — safe to set early
        AppConfig.Set("gpu_auto", targetAuto ? 1 : 0);
        // NOTE: SaveModeToConfig is called at each SUCCESS exit point below, not here.
        // If a hardware write fails, config must NOT say the new mode.

        // ── Exit Eco first if MUX change is needed ──
        // gpu_mux_mode write FAILS when dgpu_disable=1 (firmware rejects "No such device").
        // Must enable dGPU before any MUX change.
        if (currentEco && currentMux >= 0 && currentMux != targetMux)
        {
            Logger.WriteLine("GpuModeController: currently in Eco, enabling dGPU before MUX change");
            try
            {
                _wmi.SetGpuEco(false);
                RemoveDriverBlock();

                // Verify dGPU re-enablement — Windows G-Helper pattern: wait + readback.
                // SetGpuEco(false) includes 50ms settle + PCI rescan (Phase 1).
                // Additional 200ms here for firmware to update dgpu_disable readback.
                Thread.Sleep(200);
                if (_wmi.GetGpuEco())
                {
                    Logger.WriteLine("GpuModeController: FAILED to exit Eco — dgpu_disable still reads 1 after 200ms, aborting MUX change");
                    return GpuSwitchResult.Failed;
                }

                currentEco = false;
                Logger.WriteLine("GpuModeController: dGPU re-enabled, dgpu_disable readback confirmed 0");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: failed to exit Eco before MUX change: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
        }

        // ── MUX change needed? ──
        if (currentMux >= 0 && currentMux != targetMux)
        {
            Logger.WriteLine($"GpuModeController: MUX change {currentMux} → {targetMux}");
            try
            {
                _wmi.SetGpuMuxMode(targetMux);
            }
            catch (InvalidOperationException ex)
            {
                // Safety guard violation (dgpu_disable=1) — shouldn't happen after Eco exit above
                Logger.WriteLine($"GpuModeController: MUX write safety violation: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            catch (Exception ex)
            {
                // IOException from firmware rejection, or other unexpected error
                Logger.WriteLine($"GpuModeController: MUX write failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            _pendingMuxLatch = targetMux;
            if (targetMux == 0)
                SetMuxZeroLatchFlag();

            if (currentEco != targetEco && targetEco)
            {
                // MUX change + Eco needed — BUT validate this isn't an impossible combo.
                // If MUX is latched to 0 (Ultimate), Eco block artifacts would cause black screen.
                // WriteDriverBlock already refuses, but be explicit here too.
                if (WouldCreateImpossibleState(target))
                {
                    // MUX latched to 0 + Eco = impossible. This shouldn't happen from
                    // normal UI flow (Eco has targetMux=1), but defend against it.
                    Logger.WriteLine("GpuModeController: MUX change + Eco creates impossible state — Eco blocked");
                    RemoveDriverBlock();
                    return GpuSwitchResult.EcoBlocked;
                }

                // Check if driver is blocking.
                // If driver is active, return DriverBlocking so the UI shows the dialog
                // on the FIRST click (not silently latching MUX and forcing a second click).
                if (IsDgpuDriverActive())
                {
                    Logger.WriteLine("GpuModeController: MUX change + Eco needed, driver active → DriverBlocking");
                    // MUX is already latched above. The dialog's "After Reboot" will call
                    // ScheduleModeForReboot() which writes the block artifacts.
                    // ScheduleModeForReboot also has the impossible-state guard.
                    SaveModeToConfig(target);
                    return GpuSwitchResult.DriverBlocking;
                }
                Logger.WriteLine("GpuModeController: also need Eco — deferred to after MUX settles (next boot)");
            }
            else if (!targetEco)
            {
                // Switching to Standard/Ultimate/Optimized — clean up stale block artifacts
                RemoveDriverBlock();
            }

            SaveModeToConfig(target);
            return GpuSwitchResult.RebootRequired;
        }

        // ── dgpu change needed? ──
        if (currentEco == targetEco)
        {
            Logger.WriteLine($"GpuModeController: hardware already in target state (eco={currentEco})");
            // Clean up stale block artifacts if target is not Eco
            if (!targetEco) RemoveDriverBlock();
            SaveModeToConfig(target);
            return GpuSwitchResult.AlreadySet;
        }

        if (!targetEco)
        {
            // Enabling dGPU — always safe, always fast
            var result = ExecuteEnableDgpu();
            if (result == GpuSwitchResult.Applied)
                SaveModeToConfig(target);
            return result;
        }
        else
        {
            // Disabling dGPU — THE dangerous path
            var result = ExecuteDisableDgpu();
            if (result == GpuSwitchResult.Applied)
                SaveModeToConfig(target);
            else if (result == GpuSwitchResult.DriverBlocking || result == GpuSwitchResult.RebootRequired)
                SaveModeToConfig(target);
            // On Failed: do NOT save config
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Atomic operations
    // ════════════════════════════════════════════════════════════════

    /// <summary>Always safe. Write dgpu_disable=0 (enable dGPU). Returns Applied.</summary>
    private GpuSwitchResult ExecuteEnableDgpu()
    {
        Logger.WriteLine("GpuModeController: enabling dGPU (dgpu_disable=0) — always safe");
        try
        {
            _wmi.SetGpuEco(false);
            // Switching away from Eco — remove block artifacts (dGPU driver should be loadable)
            RemoveDriverBlock();
            Logger.WriteLine("GpuModeController: dGPU enabled");
            return GpuSwitchResult.Applied;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: enable dGPU failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
    }

    /// <summary>
    /// THE one dangerous operation. Checks driver safety first.
    /// If safe → writes dgpu_disable=1 (may block 30-60s).
    /// If unsafe → returns DriverBlocking.
    /// </summary>
    private GpuSwitchResult ExecuteDisableDgpu()
    {
        // Check if in Ultimate mode (MUX=0) — kernel refuses dgpu_disable=1
        int mux = GetEffectiveMux();
        if (mux == 0)
        {
            Logger.WriteLine("GpuModeController: MUX=0 (Ultimate) — cannot disable dGPU directly");
            // Latch MUX change — dgpu_disable must wait until MUX settles on next boot.
            // Do NOT write block here — MUX needs to settle first. ApplyPendingOnStartup()
            // will write the block after confirming MUX is correct.
            try
            {
                _wmi.SetGpuMuxMode(1);
                _pendingMuxLatch = 1;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"GpuModeController: ExecuteDisableDgpu — MUX latch failed: {ex.Message}");
                return GpuSwitchResult.Failed;
            }
            return GpuSwitchResult.RebootRequired;
        }

        // Check driver safety
        if (IsDgpuDriverActive())
        {
            Logger.WriteLine("GpuModeController: dGPU driver is ACTIVE — returning DriverBlocking");
            return GpuSwitchResult.DriverBlocking;
        }

        // Safe to write
        Logger.WriteLine("GpuModeController: dGPU driver idle/absent — writing dgpu_disable=1");
        try
        {
            _wmi.SetGpuEco(true);

            // Verify the write took effect
            if (_wmi.GetGpuEco())
            {
                Logger.WriteLine("GpuModeController: dgpu_disable=1 confirmed");
                // Eco applied live — remove block artifacts (dgpu_disable=1 is persistent)
                RemoveDriverBlock();
                return GpuSwitchResult.Applied;
            }
            else
            {
                Logger.WriteLine("GpuModeController: dgpu_disable=1 write did not take effect");
                return GpuSwitchResult.Failed;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: dgpu_disable=1 write failed: {ex.Message}");
            return GpuSwitchResult.Failed;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Driver detection
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Check if the dGPU driver is currently active (holding the hardware).
    /// NVIDIA: check /sys/module/nvidia_drm/refcnt
    /// AMD: check dGPU PCI device power/runtime_status
    /// </summary>
    public bool IsDgpuDriverActive()
    {
        if (IsNvidiaGpu())
            return IsNvidiaDriverActive();

        if (IsAmdDgpu())
            return IsAmdDriverActive();

        // No known dGPU driver loaded — safe
        Logger.WriteLine("GpuModeController: no dGPU driver detected — safe");
        return false;
    }

    private bool IsNvidiaDriverActive()
    {
        // Check if nvidia_drm module is loaded
        if (!Directory.Exists("/sys/module/nvidia_drm"))
        {
            Logger.WriteLine("GpuModeController: nvidia_drm module not loaded — safe");
            return false;
        }

        // Read refcnt — if > 0, the display stack has the GPU open
        int refcnt = ReadNvidiaDrmRefcount();
        if (refcnt < 0)
        {
            // Can't read refcnt — assume active for safety
            Logger.WriteLine("GpuModeController: nvidia_drm loaded but can't read refcnt — assuming active");
            return true;
        }

        if (refcnt == 0)
        {
            Logger.WriteLine("GpuModeController: nvidia_drm refcnt=0 — driver idle, safe");
            return false;
        }

        Logger.WriteLine($"GpuModeController: nvidia_drm refcnt={refcnt} — driver ACTIVE");
        return true;
    }

    private bool IsAmdDriverActive()
    {
        string? pciAddr = FindDgpuPciAddress();
        if (pciAddr == null)
        {
            Logger.WriteLine("GpuModeController: AMD dGPU PCI address not found — assuming safe");
            return false;
        }

        string status = ReadDgpuRuntimeStatus(pciAddr);
        if (status == "suspended")
        {
            Logger.WriteLine($"GpuModeController: AMD dGPU {pciAddr} runtime_status=suspended — safe");
            return false;
        }

        Logger.WriteLine($"GpuModeController: AMD dGPU {pciAddr} runtime_status={status} — ACTIVE");
        return true;
    }

    // ════════════════════════════════════════════════════════════════
    //  Driver release
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to release the dGPU driver so dgpu_disable=1 can proceed safely.
    /// NVIDIA: pkexec rmmod nvidia stack
    /// AMD: pkexec PCI unbind+remove
    /// Returns true if driver was released.
    /// </summary>
    private bool TryReleaseGpuDriver()
    {
        if (IsNvidiaGpu())
            return TryReleaseNvidiaDriver();

        if (IsAmdDgpu())
            return TryReleaseAmdDriver();

        return true; // No driver to release
    }

    private bool TryReleaseNvidiaDriver()
    {
        Logger.WriteLine("GpuModeController: attempting NVIDIA driver release via pkexec rmmod");

        // Unload in dependency order
        string modules = "nvidia_drm nvidia_modeset nvidia_uvm nvidia nvidia_wmi_ec_backlight";
        string? result = SysfsHelper.RunCommandWithTimeout(
            "pkexec", $"rmmod {modules}", 120000);

        // Check if modules are gone
        if (!Directory.Exists("/sys/module/nvidia_drm"))
        {
            Logger.WriteLine("GpuModeController: NVIDIA modules unloaded successfully");
            return true;
        }

        // Some modules might have failed — check refcnt
        int refcnt = ReadNvidiaDrmRefcount();
        if (refcnt == 0)
        {
            // Try again without nvidia_wmi_ec_backlight (it might not exist)
            Logger.WriteLine("GpuModeController: nvidia_drm still loaded but refcnt=0, retrying without wmi_ec_backlight");
            SysfsHelper.RunCommandWithTimeout("pkexec", "rmmod nvidia_drm nvidia_modeset nvidia_uvm nvidia", 120000);

            if (!Directory.Exists("/sys/module/nvidia_drm"))
            {
                Logger.WriteLine("GpuModeController: NVIDIA modules unloaded on retry");
                return true;
            }
        }

        Logger.WriteLine("GpuModeController: NVIDIA driver release failed — modules still loaded");
        return false;
    }

    private bool TryReleaseAmdDriver()
    {
        string? pciAddr = FindDgpuPciAddress();
        if (pciAddr == null)
        {
            Logger.WriteLine("GpuModeController: AMD dGPU PCI address not found");
            return false;
        }

        Logger.WriteLine($"GpuModeController: attempting AMD dGPU PCI unbind+remove for {pciAddr}");

        // Unbind from amdgpu driver, then remove from PCI bus
        string cmd = $"sh -c 'echo {pciAddr} > /sys/bus/pci/drivers/amdgpu/unbind && echo 1 > /sys/bus/pci/devices/{pciAddr}/remove'";
        string? result = SysfsHelper.RunCommandWithTimeout("pkexec", cmd, 120000);

        // Verify: the PCI device should no longer have a driver bound
        string driverLink = $"/sys/bus/pci/devices/{pciAddr}/driver";
        if (!File.Exists(driverLink) && !Directory.Exists(driverLink))
        {
            Logger.WriteLine($"GpuModeController: AMD dGPU {pciAddr} unbound successfully");
            return true;
        }

        Logger.WriteLine($"GpuModeController: AMD dGPU {pciAddr} unbind may have failed");
        return false;
    }

    // ════════════════════════════════════════════════════════════════
    //  Hardware detection helpers
    // ════════════════════════════════════════════════════════════════

    /// <summary>True if the NVIDIA kernel module is loaded.</summary>
    private static bool IsNvidiaGpu()
    {
        return Directory.Exists("/sys/module/nvidia");
    }

    /// <summary>True if an AMD discrete GPU is present (vendor=0x1002, boot_vga=0).</summary>
    private bool IsAmdDgpu()
    {
        return FindDgpuPciAddress() != null;
    }

    /// <summary>Read /sys/module/nvidia_drm/refcnt. Returns -1 if not readable.</summary>
    private static int ReadNvidiaDrmRefcount()
    {
        return SysfsHelper.ReadInt("/sys/module/nvidia_drm/refcnt", -1);
    }

    /// <summary>Read power/runtime_status for a PCI device. Returns "active"/"suspended"/etc.</summary>
    private static string ReadDgpuRuntimeStatus(string pciAddr)
    {
        string path = $"/sys/bus/pci/devices/{pciAddr}/power/runtime_status";
        return SysfsHelper.ReadAttribute(path) ?? "active";
    }

    /// <summary>
    /// Scan /sys/bus/pci/devices for the AMD discrete GPU.
    /// Looks for vendor=0x1002, class=0x0300xx or 0x0302xx, boot_vga=0.
    /// Caches the result.
    /// </summary>
    private string? FindDgpuPciAddress()
    {
        if (_dgpuPciScanned) return _cachedDgpuPciAddress;
        _dgpuPciScanned = true;

        try
        {
            string pciDir = "/sys/bus/pci/devices";
            if (!Directory.Exists(pciDir)) return null;

            foreach (var deviceDir in Directory.GetDirectories(pciDir))
            {
                string? vendor = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "vendor"));
                if (vendor != "0x1002") continue; // Not AMD

                string? cls = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "class"));
                if (cls == null) continue;
                // VGA: 0x030000, 3D controller: 0x030200
                if (!cls.StartsWith("0x0300") && !cls.StartsWith("0x0302")) continue;

                string? bootVga = SysfsHelper.ReadAttribute(Path.Combine(deviceDir, "boot_vga"));
                if (bootVga == "1") continue; // This is the iGPU, skip

                // Confirm it has a DRM subsystem
                if (!Directory.Exists(Path.Combine(deviceDir, "drm"))) continue;

                _cachedDgpuPciAddress = Path.GetFileName(deviceDir);
                Logger.WriteLine($"GpuModeController: found AMD dGPU at {_cachedDgpuPciAddress}");
                return _cachedDgpuPciAddress;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: PCI scan failed: {ex.Message}");
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════════
    //  Boot safety (supergfxctl pattern)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detect and fix the impossible state: gpu_mux_mode=0 (Ultimate) + dgpu_disable=1.
    /// This causes boot hangs because the dGPU is the sole display output in MUX=0
    /// but it's been powered off. Force dgpu_disable=0 to recover.
    ///
    /// Also removes any stale block artifacts that could prevent the dGPU driver from loading.
    ///
    /// Inspired by supergfxctl's asus_boot_safety_check().
    /// </summary>
    private void BootSafetyCheck()
    {
        try
        {
            int mux = _wmi.GetGpuMuxMode();
            bool ecoEnabled = _wmi.GetGpuEco();

            if (mux == 0 && ecoEnabled)
            {
                Logger.WriteLine("GpuModeController: BOOT SAFETY — MUX=0 + dgpu_disable=1 is impossible!");
                Logger.WriteLine("GpuModeController: BOOT SAFETY — forcing dgpu_disable=0 to recover");
                _wmi.SetGpuEco(false);
                // Remove block artifacts to allow dGPU driver to load after recovery
                RemoveDriverBlock();
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: BootSafetyCheck failed: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Driver block — prevent dGPU driver loading + remove PCI devices for Eco boot
    // ════════════════════════════════════════════════════════════════

    /// <summary>Path to modprobe.d file that blocks dGPU driver loading (NVIDIA + AMD).</summary>
    private const string ModprobeBlockPath = "/etc/modprobe.d/ghelper-gpu-block.conf";

    /// <summary>Path to udev rule that removes dGPU PCI devices from the bus (NVIDIA + AMD).</summary>
    private const string UdevRemovePath = "/etc/udev/rules.d/50-ghelper-remove-dgpu.rules";

    /// <summary>Path to trigger file read by ghelper on startup.</summary>
    private const string TriggerPath = "/etc/ghelper/pending-gpu-mode";

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
        if (_helperPathScanned) return _cachedHelperPath;
        _helperPathScanned = true;

        foreach (var path in HelperSearchPaths)
        {
            if (File.Exists(path))
            {
                _cachedHelperPath = path;
                Logger.WriteLine($"GpuModeController: GPU block helper found at {path}");
                return path;
            }
        }

        Logger.WriteLine("GpuModeController: GPU block helper not found — will use pkexec fallback");
        return null;
    }

    /// <summary>Content for the modprobe.d block file (vendor-aware: NVIDIA + AMD).</summary>
    private const string ModprobeBlockContent =
        "# ghelper: block dGPU driver modules so dGPU can be safely disabled on next boot\n" +
        "# Auto-generated — will be removed after Eco mode is applied\n" +
        "# Uses 'install /bin/false' (strongest block — prevents loading by ANY means)\n" +
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
        "# Auto-generated — will be removed after Eco mode is applied\n" +
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

    /// <summary>
    /// True if any driver block artifacts (current or legacy) exist on disk.
    /// Used to decide if cleanup is needed — avoids unnecessary pkexec prompts.
    /// </summary>
    private static bool DriverBlockExists()
    {
        return File.Exists(ModprobeBlockPath)
            || File.Exists(UdevRemovePath)
            || File.Exists(TriggerPath);
    }

    /// <summary>
    /// Write three artifacts that prevent dGPU drivers from loading on the next boot,
    /// allowing ghelper to safely write dgpu_disable=1 at startup.
    ///
    /// The EnvyControl-proven approach (1.8k+ stars, no systemd service):
    ///
    /// 1. modprobe.d `install /bin/false` — the STRONGEST modprobe block.
    ///    Unlike `blacklist` (which only prevents autoload and can be overridden
    ///    by dependencies), `install /bin/false` replaces `modprobe nvidia` with
    ///    a no-op. Blocks both NVIDIA and AMD dGPU modules.
    ///
    /// 2. udev rule `ATTR{remove}="1"` — belt and suspenders.
    ///    Physically removes all dGPU PCI devices from the bus when they appear.
    ///    Even if the modprobe block somehow fails (e.g. nvidia in initramfs),
    ///    there's no PCI device for the driver to bind to.
    ///
    /// 3. Trigger file `/etc/ghelper/pending-gpu-mode` — tells ghelper on
    ///    startup to write dgpu_disable=1 and clean up.
    ///
    /// Prefers the sudo helper script (installed by install-local.sh) which needs
    /// no tty/polkit and works from autostart. Falls back to pkexec if the helper
    /// is not installed.
    ///
    /// Only Eco needs the block. All other modes want the dGPU driver available.
    /// </summary>
    private void WriteDriverBlock(GpuMode target)
    {
        try
        {
            if (target != GpuMode.Eco)
            {
                // Non-Eco: dGPU driver should be available — remove block if it exists
                RemoveDriverBlock();
                return;
            }

            // SAFETY: Never write Eco block artifacts when MUX is latched to 0 (Ultimate).
            // After reboot, MUX=0 means dGPU is the sole display — blocking dGPU driver and
            // writing dgpu_disable=1 would cause a black screen (impossible state).
            if (WouldCreateImpossibleState(target))
            {
                Logger.WriteLine("GpuModeController: WriteDriverBlock REFUSED — Eco + MUX=0 is impossible state, removing any stale artifacts instead");
                RemoveDriverBlock();
                return;
            }

            Logger.WriteLine("GpuModeController: writing driver block (modprobe + udev + trigger) for Eco boot");

            // Write file contents to /tmp (no privilege needed, no shell quoting issues)
            string tmpModprobe = Path.Combine(Path.GetTempPath(), "ghelper-gpu-block.conf");
            string tmpUdev = Path.Combine(Path.GetTempPath(), "50-ghelper-remove-dgpu.rules");

            File.WriteAllText(tmpModprobe, ModprobeBlockContent);
            File.WriteAllText(tmpUdev, UdevRemoveContent);

            // Try sudo helper first (works from autostart, no tty needed)
            // Falls back to pkexec (needs graphical polkit agent or tty)
            // Convert target mode to string for trigger file (boot script reads this)
            string modeStr = target switch
            {
                GpuMode.Eco => "eco",
                GpuMode.Standard => "standard",
                GpuMode.Optimized => "optimized",
                GpuMode.Ultimate => "ultimate",
                _ => "eco"
            };

            string? helper = FindHelperScript();
            if (helper != null)
            {
                Logger.WriteLine($"GpuModeController: using sudo helper: {helper}");
                SysfsHelper.RunCommandWithTimeout("sudo", $"{helper} write {tmpModprobe} {tmpUdev} {modeStr}", 120000);
            }
            else
            {
                Logger.WriteLine("GpuModeController: using pkexec fallback");
                SysfsHelper.RunCommandWithTimeout("pkexec",
                    $"bash -c 'mkdir -p /etc/ghelper && " +
                    $"install -m 644 {tmpModprobe} {ModprobeBlockPath} && " +
                    $"install -m 644 {tmpUdev} {UdevRemovePath} && " +
                    $"echo {modeStr} > {TriggerPath}'", 120000);
            }

            // Clean up temp files
            try { File.Delete(tmpModprobe); } catch { }
            try { File.Delete(tmpUdev); } catch { }

            if (File.Exists(TriggerPath))
            {
                Logger.WriteLine($"GpuModeController: driver block artifacts written successfully (mode={modeStr})");
                Logger.WriteLine($"  modprobe: {ModprobeBlockPath}");
                Logger.WriteLine($"  udev:     {UdevRemovePath}");
                Logger.WriteLine($"  trigger:  {TriggerPath} (content: {modeStr})");
            }
            else
            {
                Logger.WriteLine("GpuModeController: driver block write failed (pkexec cancelled or error)");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: WriteDriverBlock failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove all driver block artifacts (current + legacy from previous approaches).
    /// Called when:
    /// - Eco is applied live (dgpu_disable=1 is persistent, block no longer needed)
    /// - Switching away from Eco (dGPU driver should be loadable)
    /// - Startup confirms hardware matches config
    /// - Boot safety check (MUX=0 + dgpu_disable=1 recovery)
    ///
    /// Prefers sudo helper (no tty needed). Falls back to pkexec.
    /// No-op if no artifacts exist (avoids unnecessary sudo/pkexec calls).
    /// </summary>
    private void RemoveDriverBlock()
    {
        try
        {
            if (!DriverBlockExists())
                return; // Nothing to remove

            Logger.WriteLine("GpuModeController: removing driver block artifacts (current + legacy)");

            string? helper = FindHelperScript();
            if (helper != null)
            {
                // Helper already handles both current and legacy files (Phase 6)
                SysfsHelper.RunCommandWithTimeout("sudo", $"{helper} clean", 120000);
            }
            else
            {
                SysfsHelper.RunCommandWithTimeout("pkexec",
                    $"rm -f {ModprobeBlockPath} {UdevRemovePath} {TriggerPath}", 120000);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GpuModeController: RemoveDriverBlock failed: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Config helpers
    // ════════════════════════════════════════════════════════════════

    private static void SaveModeToConfig(GpuMode mode)
    {
        string modeStr = mode switch
        {
            GpuMode.Eco => "eco",
            GpuMode.Standard => "standard",
            GpuMode.Optimized => "optimized",
            GpuMode.Ultimate => "ultimate",
            _ => "standard"
        };
        AppConfig.Set("gpu_mode", modeStr);
    }

    private static GpuMode ParseGpuMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "eco" => GpuMode.Eco,
            "standard" => GpuMode.Standard,
            "optimized" => GpuMode.Optimized,
            "ultimate" => GpuMode.Ultimate,
            _ => GpuMode.Standard
        };
    }
}
