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

# 6. Summary
echo "[6/6] Done."
echo ""
echo "=== Installation Complete ==="
echo ""
echo "  Binary:    /usr/local/bin/ghelper-linux"
echo "  Libraries: /usr/local/lib/lib*.so"
echo "  udev:      /etc/udev/rules.d/90-ghelper.rules  (reloaded)"
echo "  Desktop:   /usr/share/applications/ghelper-linux.desktop"
echo "  Autostart: ~/.config/autostart/ghelper-linux.desktop"
echo ""
echo "Launch: ghelper-linux"
