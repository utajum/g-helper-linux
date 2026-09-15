/*
 * gpu-helper - single root entry point for g-helper's privileged GPU operations.
 *
 * Installed at /opt/ghelper/gpu-helper (root:root 755) and invoked through ONE
 * bare-path sudoers NOPASSWD entry. Every subcommand validates its arguments
 * against a hardcoded whitelist, it just funnels them through one
 * validated dispatcher. No shell is ever invoked; passthrough subcommands
 * execv the real tool directly so exit codes and stderr propagate verbatim.
 *
 * Subcommands:
 *
 *   list [skip_pid]
 *       Enumerate dGPU holders. Output (one TSV line per holder):
 *         <pid>\t<fdCount>\t<uid>\t<comm>\t<libsMapped>\t<serviceUnit>
 *       A /dev/nvidia* mapping with no open fd still pins the driver and is
 *       reported as fdCount=1. serviceUnit is the systemd unit owning the
 *       process (from /proc/<pid>/cgroup) or "-" when not part of a service.
 *
 *   kill <signal> <pid> [pid...]
 *       Service-aware kill. signal is -15 or -9. For each holder pid that
 *       belongs to a systemd unit, stop that unit first (system service via
 *       systemctl stop, user service via runuser ... systemctl --user stop) so
 *       it cannot respawn, then send the signal to the pid regardless.
 *
 *   daemon <stop|start|reset-failed|try-restart> <nvidia-powerd|nvidia-persistenced>
 *       systemctl <verb> <unit> for the two NVIDIA daemons only.
 *
 *   rmmod <module>
 *       rmmod a whitelisted NVIDIA module. stderr/exit pass through so the
 *       caller can parse "not currently loaded" / "Permission denied" etc.
 *
 *   pci-unbind <driver> <bdf> | pci-bind <driver> <bdf>
 *       Write <bdf> to /sys/bus/pci/drivers/<driver>/{unbind,bind}.
 *       driver is restricted to nvidia | snd_hda_intel | amdgpu; bdf is validated.
 *
 *   pci-remove <bdf>
 *       Write 1 to /sys/bus/pci/devices/<bdf>/remove to drop the device node
 *       from the bus (AMD dGPU Eco release: unbind then remove each function).
 *       bdf is validated.
 *
 *   slot-power <slot> <0|1>
 *       Write <0|1> to /sys/bus/pci/slots/<slot>/power to power a PCIe hotplug
 *       slot off/on. Used to re-power the dGPU's slot on Eco->Standard (a bus
 *       rescan alone cannot wake a slot left at power=0). slot is validated as a
 *       bare slot name; value must be 0 or 1.
 *
 *   pci-power <bdf> <auto|on>
 *       Write <auto|on> to /sys/bus/pci/devices/<bdf>/power/control to set the
 *       device's runtime power management (auto = allow autosuspend). Applied to
 *       the dGPU after Standard re-enable. bdf validated; value auto|on only.
 *
 *   vulkan-icd <hide|show>
 *       Rename the NVIDIA Vulkan ICD manifest aside (hide, on Eco) or back
 *       (show, on Standard) so Vulkan falls back cleanly to the iGPU while the
 *       dGPU is disabled. Hardcoded path; no-ops if the file is already in the
 *       target state.
 *
 *   smi <flag> [value]
 *       nvidia-smi with a whitelisted write flag (-pl | -lgc | -rgc | -lmc | -rmc).
 *
 *   modprobe uvcvideo | modprobe -r uvcvideo | modprobe nvidia-wmi-ec-backlight
 *       Load/unload the whitelisted modules only.
 *
 *   nvml-temp
 *       Read GPU temperature, utilization, clocks, VRAM, and P-state via NVML
 *       in a single fast round-trip (~2-5 ms). Output: space-separated
 *       key=value pairs (temp=N util=N clock=N mem-clock=N vram-used=N
 *       vram-total=N pstate=N). Keys are omitted when NVML returns
 *       NOT_SUPPORTED (e.g. Pascal has no power sensor). Designed for the
 *       tray poll path as a lightweight alternative to nvidia-smi (~200 ms).
 *
 *   nvml-clocks <core_mhz> <mem_mhz>
 *       Apply NVIDIA graphics + memory clock offsets at P0 via NVML (root-only).
 *       Prefers the modern per-pstate nvmlDeviceSetClockOffsets API (driver
 *       555+); falls back to the legacy global VF-curve setters on older
 *       drivers. libnvidia-ml.so.1 is dlopen'd so no -dev headers are needed.
 *
 *   nvml-info
 *       Read NVML driver version + current/min/max GPC & Mem clock offsets and
 *       print them as key=value lines (driver=, core-offset=, mem-offset=,
 *       core-range=min,max, mem-range=min,max, lock-gpu=, lock-mem=,
 *       power-mgmt= capability flags). Lets ghelper read the values
 *       without ever opening /dev/nvidia* in its own process (a persistent NVML
 *       handle there would block the live Eco PCI unbind).
 *
 *   nvml-procs
 *       List pids with a live GPU context straight from the driver
 *       (nvmlDeviceGetGraphicsRunningProcesses + ComputeRunningProcesses, all
 *       devices). One "<pid>\t<graphics|compute>" line each. Cross-check
 *       source for the /proc scan; also surfaces MPS clients.
 *
 *   wmi-dsts <devid> | wmi-devs <devid> <ctrl_param> | wmi-probe
 *       Raw asus-nb-wmi debugfs DSTS/DEVS access (root-only debugfs). devid is
 *       restricted to the known GPU / eGPU ACPI device IDs; ctrl_param is the
 *       data value. wmi-probe DSTS-reads all whitelisted IDs, one line each.
 *
 *   ec-fanctl
 *       Manual fan loop over the ASUS EC HealthyTable ports 0x25C/0x25D
 *       (the MyASUS fan test path, same as AsusSAIO.sys). Stdin: "probe",
 *       "set <fan> <duty>" (0-255), "auto", "quit". One reply line per
 *       command. Quit, EOF or a signal hands the fans back to the EC.
 *
 *   lenovo-flip-to-start <0|1>
 *       Write the Lenovo "Flip to Start" FBSWIF UEFI variable (power on when
 *       the lid opens). Hardcoded variable path; clears the efivarfs
 *       immutable flag first. Payload: 4-byte attrs (0x7) + enabled byte.
 *
 *   msr-uv <mv>
 *       Intel CPU undervolt via the OC mailbox (MSR 0x150), applied to the core
 *       (plane 0) and cache (plane 2) voltage planes. mv is restricted to
 *       [-150, 0] (undervolt only). Prints the decoded readback ("core=<mv>
 *       cache=<mv>") so the caller can verify the offset took effect (a
 *       firmware-locked mailbox reads back 0). Non-persistent; resets on reboot.
 *
 *
 * Sudoers (installed by install.sh, bare path = any subcommand/args):
 *   (root) NOPASSWD: /opt/ghelper/gpu-helper
 *
 * Exit codes: 0 success; 1 usage / not-permitted; 2 invalid signal;
 *             3 sysfs write error; 127 exec failure.
 */

#include "gpu-helper.h"

/* ---------- shared utilities ---------- */

/* Log to both syslog (journal, tag "gpu-helper") and stderr (captured by the
 * caller). Keeps a single audit trail of every privileged action the helper
 * performs, visible via `journalctl -t gpu-helper`. */
void glog(int prio, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    vsyslog(prio, fmt, ap);
    va_end(ap);
}

/* Validate done by caller; replace this process with the tool. */
int exec_tool(const char *tool, char *const argv[])
{
    execvp(tool, argv);
    fprintf(stderr, "gpu-helper: exec %s failed: %s\n", tool, strerror(errno));
    return 127;
}

/* fork/exec argv, wait, ignore result (used for service stops in kill). */
void run_cmd(char *const argv[])
{
    pid_t child = fork();
    if (child < 0)
        return;
    if (child == 0)
    {
        int devnull = open("/dev/null", O_WRONLY);
        if (devnull >= 0)
        {
            dup2(devnull, 1);
            dup2(devnull, 2);
        }
        execvp(argv[0], argv);
        _exit(127);
    }
    int status;
    waitpid(child, &status, 0);
}

/* ---------- dispatch ---------- */

int main(int argc, char **argv)
{
    /* Stable PATH for execvp regardless of caller environment. */
    setenv("PATH", "/usr/sbin:/usr/bin:/sbin:/bin", 1);
    openlog("gpu-helper", LOG_PID, LOG_DAEMON);

    if (argc >= 2)
    {
        if (strcmp(argv[1], "list") == 0)
            return do_list(argc >= 3 ? atoi(argv[2]) : 0);
        if (strcmp(argv[1], "kill") == 0)
            return do_kill(argc, argv);
        if (strcmp(argv[1], "daemon") == 0)
            return do_daemon(argc, argv);
        if (strcmp(argv[1], "rmmod") == 0)
            return do_rmmod(argc, argv);
        if (strcmp(argv[1], "pci-unbind") == 0)
            return do_pci("unbind", argc, argv);
        if (strcmp(argv[1], "pci-bind") == 0)
            return do_pci("bind", argc, argv);
        if (strcmp(argv[1], "pci-remove") == 0)
            return do_pci_remove(argc, argv);
        if (strcmp(argv[1], "slot-power") == 0)
            return do_slot_power(argc, argv);
        if (strcmp(argv[1], "pci-power") == 0)
            return do_pci_power(argc, argv);
        if (strcmp(argv[1], "vulkan-icd") == 0)
            return do_vulkan_icd(argc, argv);
        if (strcmp(argv[1], "egl-vendor") == 0)
            return do_egl_vendor(argc, argv);
        if (strcmp(argv[1], "drm-notify-remove") == 0)
            return do_drm_notify_remove(argc, argv);
        if (strcmp(argv[1], "nvidia-refcnt-holders") == 0)
            return do_nvidia_refcnt_holders();
        if (strcmp(argv[1], "smi") == 0)
            return do_smi(argc, argv);
        if (strcmp(argv[1], "modprobe") == 0)
            return do_modprobe(argc, argv);
        if (strcmp(argv[1], "nvml-clocks") == 0)
            return do_nvml(argc, argv);
        if (strcmp(argv[1], "nvml-temp") == 0)
            return do_nvml_temp();
        if (strcmp(argv[1], "nvml-info") == 0)
            return do_nvml_info();
        if (strcmp(argv[1], "nvml-procs") == 0)
            return do_nvml_procs();
        if (strcmp(argv[1], "wmi-dsts") == 0)
            return do_wmi_dsts(argc, argv);
        if (strcmp(argv[1], "wmi-devs") == 0)
            return do_wmi_devs(argc, argv);
        if (strcmp(argv[1], "wmi-probe") == 0)
            return do_wmi_probe();
        if (strcmp(argv[1], "ec-fanctl") == 0)
            return do_ec_fanctl();
        if (strcmp(argv[1], "msr-uv") == 0)
            return do_msr_uv(argc, argv);
        if (strcmp(argv[1], "lenovo-flip-to-start") == 0)
            return do_lenovo_flip_to_start(argc, argv);
    }

    /* Backward-compatible default: bare numeric arg is the legacy skip_pid. */
    return do_list(argc >= 2 ? atoi(argv[1]) : 0);
}
