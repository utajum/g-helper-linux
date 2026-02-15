#!/usr/bin/env bash
set -euo pipefail

# G-Helper Linux — Download & Install
# Downloads the latest release and installs it system-wide.
#
# Usage:
#   curl -sL https://raw.githubusercontent.com/utajum/g-helper-linux/master/install/install.sh | sudo bash
#   # or
#   sudo ./install/install.sh

REPO="utajum/g-helper-linux"
INSTALL_DIR="/opt/ghelper"

echo "=== G-Helper Linux Installer ==="
echo ""

# Check for root
if [[ $EUID -ne 0 ]]; then
    echo "ERROR: This installer needs root privileges."
    echo "Re-run with: sudo $0"
    exit 1
fi

# Create temp working directory
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

# Download latest release files
echo "[1/6] Downloading latest release..."
for file in ghelper libSkiaSharp.so libHarfBuzzSharp.so; do
    if ! curl -fsSL "https://github.com/$REPO/releases/latest/download/$file" -o "$WORK_DIR/$file"; then
        echo "ERROR: Failed to download $file. Check your internet connection."
        echo "       https://github.com/$REPO/releases"
        exit 1
    fi
done
chmod +x "$WORK_DIR/ghelper"

# Download install assets (udev rules, desktop entry) from repo
echo "[2/6] Downloading install assets..."
for file in 90-ghelper.rules ghelper.desktop; do
    if ! curl -fsSL "https://raw.githubusercontent.com/$REPO/master/install/$file" -o "$WORK_DIR/$file"; then
        echo "ERROR: Failed to download $file"
        exit 1
    fi
done

# Install binary + native libs to /opt/ghelper/
echo "[3/6] Installing to $INSTALL_DIR/..."
mkdir -p "$INSTALL_DIR"
install -m 755 "$WORK_DIR/ghelper" "$INSTALL_DIR/ghelper"
install -m 755 "$WORK_DIR/libSkiaSharp.so" "$INSTALL_DIR/libSkiaSharp.so"
install -m 755 "$WORK_DIR/libHarfBuzzSharp.so" "$INSTALL_DIR/libHarfBuzzSharp.so"

# Create symlink in PATH
ln -sf "$INSTALL_DIR/ghelper" /usr/local/bin/ghelper

# Install udev rules and reload
echo "[4/6] Installing udev rules..."
install -m 644 "$WORK_DIR/90-ghelper.rules" /etc/udev/rules.d/90-ghelper.rules
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

# Install desktop entry
echo "[5/6] Installing desktop entry..."
install -m 644 "$WORK_DIR/ghelper.desktop" /usr/share/applications/ghelper.desktop

# Install autostart for current user
echo "[6/6] Installing autostart entry..."
REAL_USER=$(logname 2>/dev/null || echo "${SUDO_USER:-}")
if [[ -n "$REAL_USER" ]]; then
    AUTOSTART_DIR="/home/$REAL_USER/.config/autostart"
    mkdir -p "$AUTOSTART_DIR"
    install -m 644 "$WORK_DIR/ghelper.desktop" "$AUTOSTART_DIR/ghelper.desktop"
    chown "$REAL_USER:$REAL_USER" "$AUTOSTART_DIR/ghelper.desktop"
fi

echo ""
echo "=== Installation Complete ==="
echo ""
echo "  App dir:   $INSTALL_DIR/ (binary + native libs)"
echo "  Symlink:   /usr/local/bin/ghelper"
echo "  udev:      /etc/udev/rules.d/90-ghelper.rules  (reloaded)"
echo "  Desktop:   /usr/share/applications/ghelper.desktop"
echo "  Autostart: ~/.config/autostart/ghelper.desktop"
echo ""
echo "Launch: ghelper"
