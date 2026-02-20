# G-Helper Linux — TODO

## High Priority

### Fix sysfs permissions for built-in kernel modules (systemd-tmpfiles)

**Problem:** Several sysfs nodes need 0666 permissions for non-root G-Helper, but the udev rules don't fire for built-in kernel modules (e.g. `intel_pstate` on kernel 6.17 is built-in, so `SUBSYSTEM=="module", KERNEL=="intel_pstate"` never matches). The install script's one-time `chmod` is lost on reboot since sysfs is tmpfs.

**Affected nodes:**
- `/sys/devices/system/cpu/intel_pstate/no_turbo` — CPU boost toggle (confirmed broken on G614JVR)
- `/sys/devices/system/cpu/cpufreq/boost` — AMD/generic CPU boost (likely same issue)
- `/sys/firmware/acpi/platform_profile` — performance profile switching
- `/sys/module/pcie_aspm/parameters/policy` — PCIe ASPM (already known read-only on some kernels)

**Fix:** Create `install/90-ghelper.conf` (systemd tmpfiles.d config):

```ini
# /etc/tmpfiles.d/90-ghelper.conf
# G-Helper Linux — sysfs permissions for built-in kernel modules
# These nodes can't be handled by udev rules when the driver is built-in.
# systemd-tmpfiles-setup.service runs this at every boot.

# CPU boost (Intel pstate)
z /sys/devices/system/cpu/intel_pstate/no_turbo 0666 - - -

# CPU boost (AMD / generic cpufreq)
z /sys/devices/system/cpu/cpufreq/boost 0666 - - -

# ACPI platform profile (balanced/performance/low-power)
z /sys/firmware/acpi/platform_profile 0666 - - -

# PCIe ASPM policy (may still be kernel-enforced read-only)
z /sys/module/pcie_aspm/parameters/policy 0666 - - -
```

**Deployment changes in `install/install.sh`:**
1. Download `90-ghelper.conf` alongside the udev rules
2. Install to `/etc/tmpfiles.d/90-ghelper.conf`
3. Run `systemd-tmpfiles --create /etc/tmpfiles.d/90-ghelper.conf` to apply immediately
4. Keep existing udev rules as-is (they work for modular drivers, fan curves, battery, etc.)

**Notes:**
- `z` directive sets permissions only if the path exists — safe for machines without intel_pstate
- `systemd-tmpfiles-setup.service` runs early in boot, before user login
- Belt-and-suspenders: udev handles what it can, tmpfiles catches the rest

---

## Medium Priority

### Auto screen refresh rate switching (AC/battery)
Switch display refresh rate when AC power state changes. Need to figure out the Linux mechanism (xrandr/KDE API/sysfs).

### Auto performance mode on AC/battery change
Automatically switch between performance profiles when plugging in or unplugging AC power.

### FnLock toggle
Stub exists, ACPI DEVS `0x00100023` known. Need to implement the actual toggle.

### Handle more hotkey events
- Touchpad toggle (code 107)
- Sleep (code 108)
- Camera toggle (code 133)
- Airplane mode (code 136)

### Power limit sliders UI
Values currently come from config only — no sliders in the UI yet. Need to add power limit controls to the Fans/Performance window.

---

## Low Priority

### Stop GPU apps before Eco switch
Detect and warn about running GPU applications before switching to Eco mode.

### Per-key RGB visual editor UI
Visual keyboard layout for per-key RGB configuration.

### Slash Lighting / Anime Matrix
Support for ASUS Slash and Anime Matrix LED displays.

### ROG Ally / ASUS Mouse / XG Mobile
Hardware-specific features for other ASUS devices.

### Localization
Multi-language support.
