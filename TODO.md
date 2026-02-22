# G-Helper Linux — TODO

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


