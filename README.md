# G-Helper for Linux

A native Linux port of [G-Helper](https://github.com/seerge/g-helper) — the lightweight ASUS laptop control utility. Built with .NET 8 Native AOT + Avalonia UI, it produces a single ~28 MB binary that runs on any x64 Linux desktop with zero runtime dependencies.

## Features

- **Performance modes** — Silent, Balanced, Turbo (same as Windows G-Helper)
- **Custom fan curves** — Interactive 8-point drag-to-edit chart per fan
- **Battery charge limit** — Protect battery longevity (40-100%)
- **GPU mode switching** — Eco (iGPU only), Standard (hybrid), Optimized (MUX dGPU direct)
- **Power limits** — CPU PL1/PL2, NVIDIA Dynamic Boost, temp targets
- **Screen control** — Refresh rate switching, Panel Overdrive, MiniLED modes
- **Keyboard backlight** — Brightness cycling + RGB color (on supported models)
- **Display** — Brightness, gamma adjustment
- **CPU boost** — Enable/disable turbo boost
- **System tray** — Runs in background with tray icon and context menu
- **Hotkey support** — Listens for ASUS Fn key events via evdev
- **Auto-start** — XDG autostart .desktop file integration

## Requirements

- **OS:** Ubuntu 22.04+ / Debian 12+ / Fedora 38+ / Arch (any x64 Linux with glibc)
- **Desktop:** X11 or Wayland (X11 recommended for full xrandr support)
- **Kernel:** 6.2+ recommended, 6.9+ for all features
- **Kernel module:** `asus-nb-wmi` must be loaded (it is by default on ASUS laptops)

```bash
lsmod | grep asus
# Should show: asus_nb_wmi, asus_wmi
```

### Kernel version feature matrix

| Feature | Min Kernel |
|---------|-----------|
| Performance modes, fan speed, battery limit | 5.17 |
| Custom fan curves (8-point) | 5.17 |
| PPT power limits (PL1, PL2, FPPT) | 6.2 |
| GPU MUX switch | 6.1 |
| NVIDIA Dynamic Boost / Temp Target | 6.2 |
| MiniLED mode control | 6.9 |

## Installation

### One-liner install

```bash
curl -sL https://raw.githubusercontent.com/utajum/g-helper-linux/master/install/install.sh | sudo bash
```

Downloads the latest release and installs the binary, udev rules, desktop entry, and autostart.

### Manual download

Or just grab the binary and run it directly:

```bash
curl -sL https://github.com/utajum/g-helper-linux/releases/latest/download/ghelper-linux -o ghelper-linux
chmod +x ghelper-linux
./ghelper-linux
```

### Build from source

Install prerequisites:

```bash
# Ubuntu/Debian
sudo apt install dotnet-sdk-8.0 clang zlib1g-dev

# Fedora
sudo dnf install dotnet-sdk-8.0 clang zlib-devel

# Arch
sudo pacman -S dotnet-sdk clang
```

Build and install:

```bash
./build.sh
sudo ./install/install-local.sh
```

<details>
<summary>Manual build commands</summary>

```bash
# Development (JIT, fast iteration)
cd src && dotnet restore && dotnet run

# Production (Native AOT)
cd src && dotnet publish -c Release
# Output: src/bin/Release/net8.0/linux-x64/publish/ghelper-linux
```

</details>

## What the install scripts do

Both `install.sh` and `install-local.sh` set up the same things:

| What | Where |
|------|-------|
| Binary | `/usr/local/bin/ghelper-linux` |
| Shared libraries | `/usr/local/lib/` (libHarfBuzzSharp, libSkiaSharp) |
| udev rules | `/etc/udev/rules.d/90-ghelper.rules` |
| Desktop entry | `/usr/share/applications/ghelper-linux.desktop` |
| Autostart | `~/.config/autostart/ghelper-linux.desktop` |

The difference: `install.sh` downloads the release binary, `install-local.sh` uses the local build from `dist/`.

The udev rules grant non-root access to all ASUS sysfs controls (performance modes, fan curves, power limits, battery charge limit, keyboard backlight, GPU MUX, CPU boost, backlight brightness, and hotkey events).

After installation, **reboot** or reload udev:

```bash
sudo udevadm control --reload-rules && sudo udevadm trigger
```

<details>
<summary>Manual setup (without install scripts)</summary>

```bash
# udev rules
sudo cp install/90-ghelper.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger

# Desktop entry + autostart
sudo cp install/ghelper-linux.desktop /usr/share/applications/
mkdir -p ~/.config/autostart
cp install/ghelper-linux.desktop ~/.config/autostart/
```

See `install/90-ghelper.rules` for the full list of sysfs permissions.

</details>

## Configuration

Config is stored in `~/.config/ghelper-linux/config.json`. It uses the same JSON key format as Windows G-Helper, so fan curves and mode settings are compatible.

## Project Structure

```
g-helper-linux/
  build.sh                                # Build script (Native AOT)
  install/
    install.sh                            # Download + install (end users)
    install-local.sh                      # Install from local build (developers)
    90-ghelper.rules                      # udev rules
    ghelper-linux.desktop                 # Desktop entry
  src/
    Program.cs                            # Entry point
    App.axaml / App.axaml.cs              # Avalonia app + tray icon
    GHelper.Linux.csproj                  # Project file (AOT config)
    Helpers/
      Logger.cs                           # Console logger
      AppConfig.cs                        # Configuration (JSON, AOT-safe)
    Mode/
      Modes.cs                            # Performance mode definitions
      ModeControl.cs                      # Mode change orchestrator
    Platform/
      Linux/
        SysfsHelper.cs                    # Core sysfs read/write utility
        LinuxAsusWmi.cs                   # asus-wmi sysfs + evdev events
        LinuxPowerManager.cs              # CPU boost, platform profile
        LinuxDisplayControl.cs            # Backlight, xrandr, gamma
        LinuxNvidiaGpuControl.cs          # nvidia-smi monitoring
        LinuxAmdGpuControl.cs             # amdgpu sysfs monitoring
        LinuxAudioControl.cs              # PulseAudio/PipeWire
        LinuxInputHandler.cs              # evdev event forwarding
        LinuxSystemIntegration.cs         # DMI sysfs, XDG autostart
    UI/
      Styles/
        GHelperTheme.axaml                # Dark theme
      Controls/
        FanCurveChart.cs                  # Interactive fan curve chart
      Views/
        MainWindow.axaml / .cs            # Main settings window
        FansWindow.axaml / .cs            # Fan curve editor + power limits
        ExtraWindow.axaml / .cs           # Display, power, system info
      Assets/
        *.png, *.ico                      # Image assets
```

## How it works

G-Helper for Linux communicates with ASUS hardware through the same ACPI device IDs as the Windows version, but via the Linux kernel's `asus-wmi` driver instead of Windows ATKACPI:

| Windows (G-Helper) | Linux (this port) |
|---|---|
| `\\.\ATKACPI` DeviceIoControl | `/sys/devices/platform/asus-nb-wmi/` sysfs |
| DSTS (read) / DEVS (write) | `cat` / `echo >` sysfs attributes |
| WMI `Win32_*` queries | `/sys/class/dmi/id/` sysfs |
| `user32.dll` EnumDisplaySettings | `xrandr` CLI |
| NvAPIWrapper.Net | `nvidia-smi` CLI + hwmon sysfs |
| `atiadlxx.dll` (AMD ADL) | amdgpu hwmon sysfs |
| Task Scheduler autostart | XDG `~/.config/autostart/*.desktop` |
| WinForms UI | Avalonia UI (cross-platform) |

## Credits

- [G-Helper](https://github.com/seerge/g-helper) by seerge — the original Windows utility this is ported from
- [Avalonia UI](https://avaloniaui.net/) — cross-platform .NET UI framework
- [asus-wmi kernel driver](https://github.com/torvalds/linux/tree/master/drivers/platform/x86) — Linux kernel ASUS WMI support

## License

Same license as the original G-Helper project.
