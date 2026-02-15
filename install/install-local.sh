#!/usr/bin/env bash
set -euo pipefail

# G-Helper Linux — Install from local build
# Installs the binary from dist/ (produced by build.sh) plus udev rules,
# desktop entry, icon, and autostart.
#
# Usage:
#   ./build.sh
#   sudo ./install/install-local.sh

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
DIST_DIR="$PROJECT_DIR/dist"

echo "=== G-Helper Linux — Install Local Build ==="
echo ""

# Check for root
if [[ $EUID -ne 0 ]]; then
    echo "ERROR: This installer needs root privileges."
    echo "Re-run with: sudo $0"
    exit 1
fi

# Verify local build exists
if [[ ! -f "$DIST_DIR/ghelper-linux" ]]; then
    echo "ERROR: No binary found at $DIST_DIR/ghelper-linux"
    echo "Run ./build.sh first, or use ./install/install.sh to download the latest release."
    exit 1
fi

# 1. Install binary (single file — native libs are embedded)
echo "[1/6] Installing binary to /usr/local/bin/..."
install -m 755 "$DIST_DIR/ghelper-linux" /usr/local/bin/ghelper-linux

# 2. Install udev rules and reload
echo "[2/6] Installing udev rules..."
install -m 644 "$SCRIPT_DIR/90-ghelper.rules" /etc/udev/rules.d/90-ghelper.rules
udevadm control --reload-rules
udevadm trigger
echo "       udev rules reloaded and triggered"

# Apply permissions immediately for already-loaded modules/devices
# (udev trigger may not re-fire for modules loaded at boot)
echo "       Applying sysfs permissions..."
for f in \
    /sys/devices/platform/asus-nb-wmi/throttle_thermal_policy \
    /sys/devices/platform/asus-nb-wmi/panel_od \
    /sys/devices/platform/asus-nb-wmi/ppt_pl1_spl \
    /sys/devices/platform/asus-nb-wmi/ppt_pl2_sppt \
    /sys/devices/platform/asus-nb-wmi/ppt_fppt \
    /sys/devices/platform/asus-nb-wmi/nv_dynamic_boost \
    /sys/devices/platform/asus-nb-wmi/nv_temp_target \
    /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable \
    /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode \
    /sys/bus/platform/devices/asus-nb-wmi/mini_led_mode \
    /sys/module/pcie_aspm/parameters/policy \
    /sys/firmware/acpi/platform_profile \
    /sys/devices/system/cpu/intel_pstate/no_turbo \
    /sys/devices/system/cpu/cpufreq/boost \
    /sys/class/leds/asus::kbd_backlight/brightness \
    /sys/class/leds/asus::kbd_backlight/multi_intensity; do
    [ -f "$f" ] && chmod 0666 "$f" 2>/dev/null && echo "         ✓ $f" || true
done
# Battery charge limit
for f in /sys/class/power_supply/BAT*/charge_control_end_threshold; do
    [ -f "$f" ] && chmod 0666 "$f" 2>/dev/null && echo "         ✓ $f" || true
done
# Backlight
for f in /sys/class/backlight/*/brightness; do
    [ -f "$f" ] && chmod 0666 "$f" 2>/dev/null && echo "         ✓ $f" || true
done
# Fan curves (hwmon)
for hwmon in /sys/class/hwmon/hwmon*; do
    name=$(cat "$hwmon/name" 2>/dev/null)
    if [[ "$name" == "asus_nb_wmi" || "$name" == "asus_custom_fan_curve" ]]; then
        for f in "$hwmon"/pwm*_auto_point* "$hwmon"/pwm*_enable; do
            [ -f "$f" ] && chmod 0666 "$f" 2>/dev/null || true
        done
        echo "         ✓ $hwmon ($name fan curves)"
    fi
done

# 3. Install desktop entry
echo "[3/6] Installing desktop entry..."
install -m 644 "$SCRIPT_DIR/ghelper-linux.desktop" /usr/share/applications/ghelper-linux.desktop

# 4. Install icon
echo "[4/6] Installing icon..."
ICON_SRC="$PROJECT_DIR/src/UI/Assets/favicon.ico"
if [[ -f "$ICON_SRC" ]]; then
    mkdir -p /usr/share/icons/hicolor/64x64/apps
    if command -v convert &>/dev/null; then
        convert "$ICON_SRC[0]" /usr/share/icons/hicolor/64x64/apps/ghelper-linux.png 2>/dev/null
    else
        cp "$ICON_SRC" /usr/share/icons/hicolor/64x64/apps/ghelper-linux.ico
        sed -i 's|Icon=ghelper-linux|Icon=/usr/share/icons/hicolor/64x64/apps/ghelper-linux.ico|' \
            /usr/share/applications/ghelper-linux.desktop
    fi
    gtk-update-icon-cache /usr/share/icons/hicolor 2>/dev/null || true
fi

# 5. Install autostart for current user
echo "[5/6] Installing autostart entry..."
REAL_USER=$(logname 2>/dev/null || echo "${SUDO_USER:-}")
if [[ -n "$REAL_USER" ]]; then
    AUTOSTART_DIR="/home/$REAL_USER/.config/autostart"
    mkdir -p "$AUTOSTART_DIR"
    install -m 644 "$SCRIPT_DIR/ghelper-linux.desktop" "$AUTOSTART_DIR/ghelper-linux.desktop"
    chown "$REAL_USER:$REAL_USER" "$AUTOSTART_DIR/ghelper-linux.desktop"
fi

# 7. Summary
echo "[6/6] Done."
echo ""
echo "=== Installation Complete ==="
echo ""
echo "  Binary:    /usr/local/bin/ghelper-linux"
echo "  udev:      /etc/udev/rules.d/90-ghelper.rules  (reloaded)"
echo "  Desktop:   /usr/share/applications/ghelper-linux.desktop"
echo "  Autostart: ~/.config/autostart/ghelper-linux.desktop"
echo ""
echo "Launch: ghelper-linux"
