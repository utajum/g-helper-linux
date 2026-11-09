using GHelper.Linux.I18n;

namespace GHelper.Linux.Gpu;

/// <summary>Notification for a manual switch result, shared by tray and main window; null when none applies.</summary>
internal static class GpuSwitchNotice
{
    internal static (string Text, string Icon)? For(GpuSwitchResult result, GpuMode target) => result switch
    {
        GpuSwitchResult.Applied => (target switch
        {
            GpuMode.Eco => Labels.Get("gpu_notify_eco"),
            GpuMode.Standard => Labels.Get("gpu_notify_standard"),
            GpuMode.Optimized => Labels.Get("gpu_notify_optimized"),
            GpuMode.Ultimate => Labels.Get("gpu_notify_ultimate"),
            _ => Labels.Get("gpu_notify_changed")
        }, "video-display"),
        GpuSwitchResult.RebootRequired => (target switch
        {
            GpuMode.Ultimate => Labels.Get("gpu_reboot_ultimate"),
            GpuMode.Standard => Labels.Get("gpu_reboot_standard"),
            GpuMode.Optimized => Labels.Get("gpu_reboot_optimized"),
            GpuMode.Eco => Labels.Get("gpu_reboot_eco"),
            _ => Labels.Get("gpu_reboot_generic")
        }, "system-reboot"),
        GpuSwitchResult.EcoBlocked => (Labels.Get("gpu_eco_blocked_mux"), "dialog-warning"),
        GpuSwitchResult.Deferred => (Labels.Get("gpu_eco_after_reboot"), "system-reboot"),
        GpuSwitchResult.Failed => (Labels.Get("gpu_switch_failed"), "dialog-error"),
        _ => null
    };
}
