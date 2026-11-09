namespace GHelper.Linux.Gpu;

/// <summary>One gpu_backend's switching rules; GPUModeControl keeps the lock, the MUX guard and shared side effects.</summary>
internal interface IGpuBackend
{
    /// <summary>Switch to <paramref name="target"/>. Called with the switch lock held.</summary>
    GpuSwitchResult Switch(GpuMode target);

    /// <summary>"Switch Now": release the driver, then apply <paramref name="target"/> (Eco; ebpf also Optimized). Called with the switch lock held.</summary>
    GpuSwitchResult ReleaseAndSwitch(GpuMode target);

    /// <summary>"After Reboot" with the backend's own rules, or null for the shared MUX latch path.</summary>
    GpuSwitchResult? ScheduleForReboot(GpuMode target);

    /// <summary>True when the backend writes the persistent marker itself, so the persistent Eco option leaves it alone.</summary>
    bool OwnsPersistentMarker { get; }

    /// <summary>Bring the hardware in line with the saved mode at app start.</summary>
    GpuSwitchResult ApplyPendingOnStartup();

    /// <summary>Best-effort pending Eco at app exit.</summary>
    void ApplyPendingOnShutdown();

    /// <summary>Optimized: follow the power source.</summary>
    GpuSwitchResult AutoSwitch();

    GpuMode GetCurrentMode();

    bool IsPendingReboot();
}
