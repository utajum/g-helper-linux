# GA403UI Mehdi profile

This fork keeps the upstream G-Helper Linux codebase intact and adds a small
migration path for Mehdi's 2024 ROG Zephyrus G14 GA403UI.

## Goal

Carry the working Windows G-Helper setup to Arch Linux without manually
recreating every custom performance mode, CPU/GPU/Mid fan curve, battery
setting, Aura mode and GPU tuning value.

The Linux port already uses the same JSON key/value format for most settings.
The only migration currently required by this fork is the calibrated fan
maximum representation:

- Windows: `fan_max_0=76`, `fan_max_1=77`, `fan_max_2=91`
- Linux: `fan_max="7600,7700,9100"`

These are the measured values from the source GA403UI configuration, not
generic GA403 defaults.

## Import on Arch

1. Copy Windows `%AppData%\\GHelper\\config.json` to the Linux machine.
2. Stop G-Helper if it is running.
3. Preview the migration:

```bash
ghelper --import-windows-config ~/Downloads/config.json --dry-run
```

4. Apply it:

```bash
ghelper --import-windows-config ~/Downloads/config.json
```

The importer merges the Windows configuration into
`~/.config/ghelper/config.json`. If a Linux config already exists it is backed
up before modification.

## What is preserved

Custom Silent/Balanced/Turbo-derived modes, all 8-point CPU/GPU/Mid fan curves,
power limits, CPU temperature limits, AMD Curve Optimizer values, GPU tuning,
battery limit, screen-related settings, Aura/Heatmap settings and Slash keys are
carried over because their config format is shared by the Linux port.

Unknown keys are retained rather than deleted. This lets future upstream
versions start supporting a setting without requiring another import.

## Windows-only settings

ASUS GameVisual / Splendid color processing is Windows-specific. Its config keys
may remain in the JSON for portability, but G-Helper Linux does not use the
Windows Splendid backend.

## Hardware validation still required

The first real Arch boot should validate the following before treating the
profile as production-ready:

- Eco mode fully powers down the RTX dGPU.
- CPU, GPU and Mid fan RPM sensors are all present.
- All three 8-point fan curves can be applied and restored.
- Slash HID interface is detected and survives suspend/resume.
- Aura Heatmap works on the GA403UI keyboard.
- AMD Curve Optimizer is available through `ryzen_smu`.
- 80% battery charge limit is exposed by the active ASUS kernel driver.
- OLED refresh-rate switching works under the chosen desktop/compositor.

No unsafe firmware fallback is enabled by this profile. If an ASUS kernel
attribute is missing, that feature should remain unavailable until the machine
is inspected rather than forcing raw ACPI writes.


## First Arch boot: collect the real hardware map

Before changing any firmware values, run the read-only probe from the repository:

```bash
chmod +x scripts/ga403ui-probe.sh
./scripts/ga403ui-probe.sh
```

It creates a timestamped `ga403ui-probe-*.txt` file. The script only reads
sysfs/device information; it does not change GPU mode, fan curves, battery
limits, Slash/Aura state or firmware attributes.

That file is the source of truth for the final GA403UI-specific layer. In
particular it lets us verify which `asus-armoury` attributes exist on the
installed kernel and which HID interface the 2024 Slash device actually exposes.
