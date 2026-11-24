# AMD Ryzen Undervolting (Curve Optimizer)

G-Helper supports AMD Ryzen Curve Optimizer (CO) undervolting via the
[ryzen_smu](https://github.com/amkillam/ryzen_smu) kernel driver.

The feature is **hidden** unless the driver is loaded and the CPU is supported.
CO values **reset on every reboot** for safety.

---

## Requirements

- AMD Ryzen CPU (see supported list below)
- [ryzen_smu](https://github.com/amkillam/ryzen_smu) kernel module loaded
- G-Helper udev rules installed (provides write access to `/sys/kernel/ryzen_smu_drv/`)

## Supported CPUs

| Codename | Generation | Status |
|----------|-----------|--------|
| Raphael / Dragon Range | Zen 4 (Ryzen 7000 desktop / 7045HX mobile) | Tested |
| Phoenix | Zen 4 APU (Ryzen 7040) | Enabled (MP1, not validated) |
| Hawk Point | Zen 4 refresh (Ryzen 8000 mobile) | Enabled (MP1, not validated) |
| Vermeer | Zen 3 (Ryzen 5000 desktop) | Enabled (validated at startup) |
| Cezanne | Zen 3 APU (Ryzen 5000G) | Enabled (MP1, not validated) |
| Rembrandt | Zen 3+ APU (Ryzen 6000 mobile) | Enabled (MP1, not validated) |
| Granite Ridge | Zen 5 (Ryzen 9000 desktop) | Enabled (validated at startup) |
| Strix Point | Zen 5 mobile (Ryzen AI 300) | Enabled (MP1, not validated) |
| Strix Halo | Zen 5 (Ryzen AI 300 HX) | Enabled (MP1, not validated) |

Vermeer, Raphael and Granite Ridge are protected by a startup validation probe - if the SMU rejects
the read-only test command, CO disables itself before any writes happen.
APUs (Cezanne, Rembrandt, Phoenix, Hawk Point, Strix Point, Strix Halo)
use the MP1 mailbox like Windows G-Helper and ryzenadj. MP1 has no read command,
so these have no probe and no read-back.

## How it works

Curve Optimizer adjusts the voltage-frequency curve per core. Negative offsets
lower voltage at each frequency point, reducing power and heat while maintaining
the same clock speeds. This allows the CPU to boost higher within the same
thermal/power budget.

G-Helper sends SMU mailbox commands via the driver's sysfs interface:
- `smu_args` - 24 bytes (6x uint32 LE) command arguments
- `rsmu_cmd`, or `mp1_smu_cmd` on APUs - 4 bytes, triggers the command and
  blocks until SMU responds

## Safety

- **Validation on startup** (RSMU only): sends a read-only `GetDldoPsmMargin` probe before
  allowing any writes. If the SMU rejects it, CO is disabled entirely.
- **Range clamped**: values limited to [-40, 0] (matches Windows G-Helper)
- **Read-back verification** (RSMU only): every write is verified by reading the value back
- **Non-persistent**: resets on reboot. If unstable values cause a crash,
  a simple reboot restores stock settings.
- **Auto-apply**: optional checkbox to re-apply CO when switching performance
  modes (Silent/Balanced/Turbo). Disabled by default.
