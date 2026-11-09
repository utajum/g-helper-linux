using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;

namespace GHelper.Linux.Gpu;

/// <summary>Firmware MUX writes and the guard against MUX=0 with dgpu_disable=1; every write except the startup one goes through <see cref="Write"/>.</summary>
internal sealed class MuxLatch
{
    private readonly IHardwareControl _wmi;

    /// <summary>
    /// Tracks pending MUX latch value within this session.
    /// After SetGpuMuxMode(x), hardware still reports the OLD value until reboot.
    /// This field remembers what we latched so the switch paths
    /// use the correct effective MUX, not the stale hardware readback.
    /// -1 = no pending latch (use hardware value).
    /// </summary>
    private volatile int _pending = -1;

    public MuxLatch(IHardwareControl wmi)
    {
        _wmi = wmi;
    }

    /// <summary>
    /// Return the effective MUX value: if we latched a change this session, return
    /// the latched value. Otherwise return actual hardware state.
    /// Used by FirmwareGpuBackend.Switch(), ExecuteDisableDgpu(), ScheduleModeForReboot()
    /// methods that need to know "what MUX will be after reboot".
    /// NOT used by GetCurrentMode(), AutoGpuSwitch(), IsPendingReboot(),
    /// ApplyPendingOnStartup(), ApplyPendingOnShutdown() - those need actual hardware.
    /// </summary>
    public int Effective
    {
        get
        {
            int pending = _pending;
            return pending >= 0 ? pending : _wmi.GetGpuMuxMode();
        }
    }

    /// <summary>Latch the MUX for the next boot and record it; throws like SetGpuMuxMode and records nothing then.</summary>
    public void Write(int mux)
    {
        _wmi.SetGpuMuxMode(mux);
        _pending = mux;
        if (mux == 0)
            SetZeroFlag();
    }

    /// <summary>
    /// Check if MUX=0 was written at any point during the current boot session.
    /// Uses persistent storage (AppConfig) keyed by boot_id, so it survives app restarts
    /// within the same boot session but auto-clears after reboot.
    /// </summary>
    public static bool ZeroLatchedThisBoot()
    {
        try
        {
            string? stored = AppConfig.GetString("mux_zero_latched_boot_id");
            if (string.IsNullOrEmpty(stored))
                return false;
            string current = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            return stored == current;
        }
        catch (IOException)
        {
            // /proc/sys/kernel/random/boot_id not readable (container/chroot) - fail-open
            return false;
        }
    }

    /// <summary>
    /// Persist the MUX=0 latch flag for the current boot session.
    /// Called by <see cref="Write"/> after every successful MUX=0 write.
    /// The flag stays set until reboot (boot_id changes).
    /// </summary>
    private static void SetZeroFlag()
    {
        try
        {
            string bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            AppConfig.Set("mux_zero_latched_boot_id", bootId);
            Logger.WriteLine($"GPUModeControl: MUX=0 latch persisted - boot_id={bootId}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: failed to persist MUX=0 latch flag: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear the persistent MUX=0 latch flag (config "mux_zero_latched_boot_id")
    /// when the stored boot_id no longer matches /proc/sys/kernel/random/boot_id.
    /// The flag is intentionally session-scoped - it exists to make
    /// WouldCreateImpossibleState resilient to app restarts within the same
    /// boot, but must NOT leak across reboots or across backend switches.
    ///
    /// Called from ApplyPendingOnStartup before any backend-specific code so
    /// PCI users also benefit; otherwise WouldCreateImpossibleState would
    /// permanently refuse Eco on any system that previously latched Ultimate.
    /// </summary>
    public static void ClearStaleFlag()
    {
        try
        {
            string? storedBootId = AppConfig.GetString("mux_zero_latched_boot_id");
            if (string.IsNullOrEmpty(storedBootId))
                return;
            string currentBootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            if (storedBootId != currentBootId)
            {
                AppConfig.Set("mux_zero_latched_boot_id", "");
                Logger.WriteLine("GPUModeControl: reboot detected - cleared MUX=0 latch flag");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPUModeControl: MUX=0 latch reboot check failed: {ex.Message}");
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
    public bool WouldCreateImpossibleState(GpuMode target)
    {
        if (target != GpuMode.Eco)
            return false;

        // Check persistent flag first (survives app restart within same boot)
        if (ZeroLatchedThisBoot())
        {
            Logger.WriteLine("GPUModeControl: IMPOSSIBLE STATE PREVENTED - MUX=0 was written this boot session (persistent flag). Eco + MUX=0 = black screen.");
            return true;
        }

        // Also check in-memory latch (fast path, same session - defense in depth)
        int effectiveMux = Effective;
        if (effectiveMux == 0)
        {
            Logger.WriteLine("GPUModeControl: IMPOSSIBLE STATE PREVENTED - cannot schedule Eco when MUX is latched to 0 (Ultimate). Eco + MUX=0 = black screen.");
            return true;
        }
        return false;
    }
}
