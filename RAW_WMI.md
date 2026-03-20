# Raw WMI Mode (Experimental)

Some 2020-2021 ASUS laptops (e.g., GA401II, G512LI, G533, G713) have GPU Eco mode
in their firmware but the Linux kernel doesn't expose the `dgpu_disable` sysfs attribute.
The firmware supports the ACPI method — Windows G-Helper and Armoury Crate can toggle
GPU Eco on these models — but the kernel's presence-bit check fails and the sysfs file
is never created.

**Raw WMI mode** bypasses this limitation by calling the ACPI method directly through
the kernel's `asus-nb-wmi` debugfs interface, the same mechanism the firmware uses internally.

## To Enable

1. Open Settings → Advanced
2. Check **"Raw WMI mode (experimental)"**
3. Enter root password when prompted (writes system config)
4. Restart G-Helper

## Requirements

- `asus-nb-wmi` kernel module loaded (check: `lsmod | grep asus_nb_wmi`)
- `debugfs` mounted at `/sys/kernel/debug/` (default on all major distros)
- Root access (pkexec prompt on each GPU mode switch)

## How It Works

- Reads GPU state via `DSTS(0x00090020)` — same ACPI device ID as Windows
- Writes GPU state via `DEVS(0x00090020, value)` — firmware powers GPU on/off
- Probes Vivobook endpoint `DSTS(0x00090120)` as fallback
- All existing safety guards apply (driver-active check, MUX check)
- On reboot, a systemd boot service applies the pending mode before the display manager starts

## Diagnostics

You can probe manually to check if your firmware supports it:

```bash
echo 0x00090020 | sudo tee /sys/kernel/debug/asus-nb-wmi/dev_id
sudo cat /sys/kernel/debug/asus-nb-wmi/dsts
# If output contains "0x10000" or "0x10001" → your firmware supports GPU Eco
```

> **Note:** This feature is disabled by default and has no effect on laptops that already
> have `dgpu_disable` sysfs support. On those models, the standard sysfs path is always
> used regardless of this setting.

