[![G-Helper for Linux](screenshot.png)](screenshot.png)

*Click on the screenshot to view full size.*

```
 ██████╗       ██╗  ██╗███████╗██╗     ██████╗ ███████╗██████╗ 
██╔════╝       ██║  ██║██╔════╝██║     ██╔══██╗██╔════╝██╔══██╗
██║  ███╗█████╗███████║█████╗  ██║     ██████╔╝█████╗  ██████╔╝
██║   ██║╚════╝██╔══██║██╔══╝  ██║     ██╔═══╝ ██╔══╝  ██╔══██╗
╚██████╔╝      ██║  ██║███████╗███████╗██║     ███████╗██║  ██║
 ╚═════╝       ╚═╝  ╚═╝╚══════╝╚══════╝╚═╝     ╚══════╝╚═╝  ╚═╝
                        ██╗     ██╗███╗   ██╗██╗   ██╗██╗  ██╗ 
                        ██║     ██║████╗  ██║██║   ██║╚██╗██╔╝ 
                        ██║     ██║██╔██╗ ██║██║   ██║ ╚███╔╝  
                        ██║     ██║██║╚██╗██║██║   ██║ ██╔██╗  
                        ███████╗██║██║ ╚████║╚██████╔╝██╔╝ ██╗ 
                        ╚══════╝╚═╝╚═╝  ╚═══╝ ╚═════╝ ╚═╝  ╚═╝ 
 ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
         ╔══[ SYSTEM ]════════════════════════════════╗
         ║  >_ KERNEL: LINUX                          ║ 
         ║  >_ STATUS: ONLINE...                      ║
         ╚═══════════════════════════════[ 0x1F4 ]════╝
           ╔══════════════════════════════════════╗
           ║  ASUS LAPTOP CONTROL FOR LINUX       ║
            ╚══════════════════════════════════════╝
```

## `░▒▓█ ╔══[ MOTIVATION ]══╗ █▓▒░`

Since `asusctl` doesn't really care about Ubuntu, I decided to port most functionality from the original [G-Helper](https://github.com/seerge/g-helper) for Windows.

The application is tested on KDE but other desktop environments should also work.

Pull requests and feature requests are welcome!

---

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-ffdd00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/utajum)

</div>

---

## `░▒▓█ ╔══[ FEATURES ]══╗ █▓▒░`

```
┌──────────────────────────────────────────────────────────────────┐
│  Performance modes         Silent / Balanced / Turbo            │
│  Custom fan curves         8-point drag-to-edit per fan         │
│  Battery charge limit      Protect longevity (40-100%)          │
│  GPU mode switching        Eco / Standard / Optimized (MUX)     │
│  Power limits              CPU PL1/PL2, Dynamic Boost, temps    │
│  Screen control            Refresh rate, Panel OD, MiniLED      │
│  Keyboard backlight        Brightness + RGB color               │
│  Display                   Brightness, gamma adjustment         │
│  CPU boost                 Enable/disable turbo boost            │
│  System tray               Background tray icon + context menu  │
│  Hotkey support            ASUS Fn key events via evdev         │
│  Auto-start                XDG autostart .desktop integration   │
└──────────────────────────────────────────────────────────────────┘
```

---

## `░▒▓█ ╔══[ SYSTEM REQUIREMENTS ]══╗ █▓▒░`

```
╔══[ MINIMUM SPEC ]══════════════════════════════════════════════╗
║                                                                 ║
║  OS       Ubuntu 22.04+ / Debian 12+ / Fedora 38+ / Arch      ║
║  Desktop  X11 or Wayland (X11 recommended for full xrandr)     ║
║  Kernel   6.2+ recommended, 6.9+ for all features              ║
║  Module   asus-nb-wmi (loaded by default on ASUS laptops)      ║
║                                                                 ║
╚═════════════════════════════════════════════════════════════════╝
```

```bash
# verify kernel module
lsmod | grep asus
# expected: asus_nb_wmi, asus_wmi
```

### `╠══[ KERNEL FEATURE MATRIX ]══╣`

| Feature | Min Kernel |
|---------|-----------|
| Performance modes, fan speed, battery limit | 5.17 |
| Custom fan curves (8-point) | 5.17 |
| PPT power limits (PL1, PL2, FPPT) | 6.2 |
| GPU MUX switch | 6.1 |
| NVIDIA Dynamic Boost / Temp Target | 6.2 |
| MiniLED mode control | 6.9 |

---

## `░▒▓█ ╔══[ INSTALLATION ]══╗ █▓▒░`

### `╠══[ ONE-LINER INSTALL ]══╣`

```bash
curl -sL https://raw.githubusercontent.com/utajum/g-helper-linux/master/install/install.sh | sudo bash
```

### `╠══[ MANUAL DOWNLOAD ]══╣`

```bash
curl -sL https://github.com/utajum/g-helper-linux/releases/latest/download/ghelper -o ghelper
chmod +x ghelper
./ghelper
```

### `╠══[ BUILD FROM SOURCE ]══╣`

```bash
# Ubuntu/Debian
sudo apt install dotnet-sdk-8.0 clang zlib1g-dev

# Fedora
sudo dnf install dotnet-sdk-8.0 clang zlib-devel

# Arch
sudo pacman -S dotnet-sdk clang
```

```bash
./build.sh
sudo ./install/install-local.sh
```

<details>
<summary><code>╠══[ MANUAL BUILD COMMANDS ]══╣</code></summary>

```bash
# Development (JIT, fast iteration)
cd src && dotnet restore && dotnet run

# Production (Native AOT)
cd src && dotnet publish -c Release
# → src/bin/Release/net8.0/linux-x64/publish/ghelper
```

</details>

---

## `░▒▓█ ╔══[ INSTALL TARGETS ]══╗ █▓▒░`

```
╔══[ DEPLOYED FILES ]════════════════════════════════════════════╗
║                                                                 ║
║  0xF0  Binary     /opt/ghelper/ghelper                         ║
║  0xF1  Symlink    /usr/local/bin/ghelper                       ║
║  0xF2  udev       /etc/udev/rules.d/90-ghelper.rules          ║
║  0xF3  Desktop    /usr/share/applications/ghelper.desktop      ║
║  0xF4  Autostart  ~/.config/autostart/ghelper.desktop          ║
║                                                                 ║
╚═════════════════════════════════════════════════════════════════╝
```

`install.sh` downloads the release binary. `install-local.sh` uses the local build from `dist/`.

```bash
# reload udev after install (or reboot)
sudo udevadm control --reload-rules && sudo udevadm trigger
```

<details>
<summary><code>╠══[ MANUAL SETUP ]══╣</code></summary>

```bash
# udev rules
sudo cp install/90-ghelper.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger

# Desktop entry + autostart
sudo cp install/ghelper.desktop /usr/share/applications/
mkdir -p ~/.config/autostart
cp install/ghelper.desktop ~/.config/autostart/
```

</details>

---

## `░▒▓█ ╔══[ CONFIGURATION ]══╗ █▓▒░`

```
~/.config/ghelper/config.json
```

Same JSON key format as Windows G-Helper — fan curves and mode settings are compatible.

---

## `░▒▓█ ╔══[ PROJECT STRUCTURE ]══╗ █▓▒░`

```
g-helper-linux/
  build.sh                                # Build script (Native AOT)
  install/
    install.sh                            # Download + install (end users)
    install-local.sh                      # Install from local build (devs)
    90-ghelper.rules                      # udev rules
    ghelper.desktop                       # Desktop entry
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

---

## `░▒▓█ ╔══[ ARCHITECTURE ]══╗ █▓▒░`

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

---

## `░▒▓█ ╔══[ CREDITS ]══╗ █▓▒░`

- [G-Helper](https://github.com/seerge/g-helper) by seerge
- [Avalonia UI](https://avaloniaui.net/)
- [asus-wmi kernel driver](https://github.com/torvalds/linux/tree/master/drivers/platform/x86)

---

## `░▒▓█ ╔══[ LICENSE ]══╗ █▓▒░`

Same license as the original G-Helper project.

---

<div align="center">

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-ffdd00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/utajum)

```
  ░▒▓█ END OF TRANSMISSION █▓▒░
  > SESSION_END :: 0x00000000
```

</div>
