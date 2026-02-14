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

### System
- **OS:** Ubuntu 22.04+ / Debian 12+ / Fedora 38+ / Arch (any x64 Linux with glibc)
- **Desktop:** X11 or Wayland (X11 recommended for full xrandr support)
- **Kernel:** 6.2+ recommended, 6.9+ for all features

### Kernel modules
The `asus-nb-wmi` kernel module must be loaded (it is by default on ASUS laptops):
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

## Quick Start

### Option 1: Download the pre-built binary

```bash
# Download the latest release
curl -sL https://github.com/utajum/g-helper-linux/releases/latest/download/ghelper-linux -o ghelper-linux
chmod +x ghelper-linux
./ghelper-linux
```

For full installation with desktop integration, udev rules, and autostart:
```bash
git clone https://github.com/utajum/g-helper-linux.git
cd g-helper-linux
mkdir -p dist && cp /path/to/ghelper-linux dist/
sudo ./install/install.sh
```

### Option 2: Build from source

#### Prerequisites

Install the .NET 8 SDK:
```bash
# Ubuntu/Debian
sudo apt install dotnet-sdk-8.0

# Fedora
sudo dnf install dotnet-sdk-8.0

# Arch
sudo pacman -S dotnet-sdk

# Or use the official Microsoft installer:
# https://dotnet.microsoft.com/download/dotnet/8.0
```

Install build dependencies (for AOT native compilation):
```bash
# Ubuntu/Debian
sudo apt install clang zlib1g-dev

# Fedora
sudo dnf install clang zlib-devel

# Arch
sudo pacman -S clang
```

#### Build and run (development)

```bash
cd g-helper-linux/src
dotnet restore
dotnet build
dotnet run
```

#### Publish as native AOT binary (production)

Use the build script:
```bash
cd g-helper-linux
./build.sh
```

Or manually:
```bash
cd g-helper-linux/src
dotnet publish -c Release
```

The output is in `dist/` (or `src/bin/Release/net8.0/linux-x64/publish/` for manual builds):
```
ghelper-linux          # 28 MB native ELF binary
libHarfBuzzSharp.so    # 2.1 MB (text rendering)
libSkiaSharp.so        # 8.9 MB (Skia rendering engine)
```

Then install with the install script:
```bash
sudo ./install/install.sh
```

Or copy manually:
```bash
sudo mkdir -p /opt/ghelper-linux
sudo cp dist/ghelper-linux /opt/ghelper-linux/
sudo cp dist/lib*.so /opt/ghelper-linux/
sudo chmod +x /opt/ghelper-linux/ghelper-linux
/opt/ghelper-linux/ghelper-linux
```

## Permissions (udev rules)

By default, writing to sysfs attributes requires root. The install script handles this automatically:

```bash
sudo ./install/install.sh
```

This installs udev rules (`install/90-ghelper.rules`) that grant non-root access to all ASUS hardware controls, plus the desktop entry, icon, and autostart. After installation, **reboot** or re-trigger udev:

```bash
sudo udevadm control --reload-rules && sudo udevadm trigger
```

<details>
<summary>Manual udev setup (without install script)</summary>

```bash
sudo cp install/90-ghelper.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
```

The rules file covers: performance modes, power limits, fan curves, battery charge limit, keyboard backlight, GPU MUX/Eco mode, CPU boost, backlight brightness, and ASUS hotkey events. See `install/90-ghelper.rules` for full details.

</details>

## Autostart on login

Use the built-in checkbox in the app footer, or manually:

```bash
mkdir -p ~/.config/autostart
cat > ~/.config/autostart/ghelper-linux.desktop << EOF
[Desktop Entry]
Type=Application
Name=G-Helper
Comment=ASUS Laptop Control (Linux)
Exec=/opt/ghelper-linux/ghelper-linux
Icon=ghelper-linux
Terminal=false
Categories=System;HardwareSettings;
StartupNotify=false
X-GNOME-Autostart-enabled=true
EOF
```

## Configuration

Config is stored in `~/.config/ghelper-linux/config.json`. It uses the same JSON key format as Windows G-Helper, so fan curves and mode settings are compatible.

Log output goes to `~/.config/ghelper-linux/log.txt`.

## Project Structure

```
g-helper-linux/
  GHelper.Linux.sln
  src/
    Program.cs                          # Entry point
    App.axaml / App.axaml.cs            # Avalonia app + tray icon
    GHelper.Linux.csproj                # Project file (AOT config)
    TrimmerRoots.xml                    # AOT trimmer config
    Helpers/
      Logger.cs                         # File logger
      AppConfig.cs                      # Configuration (JSON, AOT-safe)
    Mode/
      Modes.cs                          # Performance mode definitions
      ModeControl.cs                    # Mode change orchestrator
    Platform/
      IAsusWmi.cs                       # ASUS hardware interface
      IPowerManager.cs                  # CPU boost, battery, ASPM
      IGpuControl.cs                    # GPU monitoring/overclocking
      IDisplayControl.cs                # Brightness, refresh rate, gamma
      IInputHandler.cs                  # Hotkey events
      IAudioControl.cs                  # Mic/speaker mute
      ISystemIntegration.cs             # Model detection, notifications
      Linux/
        SysfsHelper.cs                  # Core sysfs read/write utility
        LinuxAsusWmi.cs                 # asus-wmi sysfs + evdev events
        LinuxPowerManager.cs            # CPU boost, platform profile
        LinuxDisplayControl.cs          # Backlight, xrandr, gamma
        LinuxNvidiaGpuControl.cs        # nvidia-smi monitoring
        LinuxAmdGpuControl.cs           # amdgpu sysfs monitoring
        LinuxAudioControl.cs            # PulseAudio/PipeWire
        LinuxInputHandler.cs            # evdev event forwarding
        LinuxSystemIntegration.cs       # DMI sysfs, XDG autostart
    UI/
      Styles/
        GHelperTheme.axaml              # Dark theme (pixel-perfect match)
      Controls/
        FanCurveChart.cs                # Custom interactive fan curve chart
      Views/
        MainWindow.axaml / .cs          # Main settings (Performance, GPU, Screen, etc.)
        FansWindow.axaml / .cs          # Fan curve editor + power limits
        ExtraWindow.axaml / .cs         # Display, power, system info, advanced
      Assets/
        *.png, *.ico                    # 85 image assets from G-Helper
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
