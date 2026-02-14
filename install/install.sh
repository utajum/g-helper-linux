#!/usr/bin/env bash
set -euo pipefail

# G-Helper Linux — Download & Install
# Downloads the latest release binary and installs it system-wide.
#
# Usage:
#   curl -sL https://raw.githubusercontent.com/utajum/g-helper-linux/master/install/install.sh | sudo bash
#   # or
#   sudo ./install/install.sh

REPO="utajum/g-helper-linux"

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

# Download latest release binary
echo "[1/6] Downloading latest release..."
if ! curl -fsSL "https://github.com/$REPO/releases/latest/download/ghelper-linux" -o "$WORK_DIR/ghelper-linux"; then
    echo "ERROR: Failed to download. Check your internet connection."
    echo "       https://github.com/$REPO/releases"
    exit 1
fi
chmod +x "$WORK_DIR/ghelper-linux"

# Download install assets (udev rules, desktop entry) from repo
echo "[2/6] Downloading install assets..."
for file in 90-ghelper.rules ghelper-linux.desktop; do
    if ! curl -fsSL "https://raw.githubusercontent.com/$REPO/master/install/$file" -o "$WORK_DIR/$file"; then
        echo "ERROR: Failed to download $file"
        exit 1
    fi
done

# Install binary
echo "[3/6] Installing binary to /usr/local/bin/..."
install -m 755 "$WORK_DIR/ghelper-linux" /usr/local/bin/ghelper-linux

# Install udev rules and reload
echo "[4/6] Installing udev rules..."
install -m 644 "$WORK_DIR/90-ghelper.rules" /etc/udev/rules.d/90-ghelper.rules
udevadm control --reload-rules
udevadm trigger
echo "       udev rules reloaded and triggered"

# Install desktop entry
echo "[5/6] Installing desktop entry..."
install -m 644 "$WORK_DIR/ghelper-linux.desktop" /usr/share/applications/ghelper-linux.desktop

# Install autostart for current user
echo "[6/6] Installing autostart entry..."
REAL_USER=$(logname 2>/dev/null || echo "${SUDO_USER:-}")
if [[ -n "$REAL_USER" ]]; then
    AUTOSTART_DIR="/home/$REAL_USER/.config/autostart"
    mkdir -p "$AUTOSTART_DIR"
    install -m 644 "$WORK_DIR/ghelper-linux.desktop" "$AUTOSTART_DIR/ghelper-linux.desktop"
    chown "$REAL_USER:$REAL_USER" "$AUTOSTART_DIR/ghelper-linux.desktop"
fi

echo ""
echo "=== Installation Complete ==="
echo ""
echo "  Binary:    /usr/local/bin/ghelper-linux"
echo "  udev:      /etc/udev/rules.d/90-ghelper.rules  (reloaded)"
echo "  Desktop:   /usr/share/applications/ghelper-linux.desktop"
echo "  Autostart: ~/.config/autostart/ghelper-linux.desktop"
echo ""
echo "Launch: ghelper-linux"
