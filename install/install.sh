#!/usr/bin/env bash
set -uo pipefail

# G-Helper Linux — Install Script
# Copies the binary, udev rules, and desktop entry to system directories.
# Run this AFTER build.sh produces the dist/ directory.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
DIST_DIR="$PROJECT_DIR/dist"

if [[ ! -d "$DIST_DIR" ]] || [[ ! -f "$DIST_DIR/ghelper-linux" ]]; then
    echo "ERROR: dist/ directory not found. Run build.sh first."
    exit 1
fi

echo "=== G-Helper Linux Installer ==="
echo ""

# Check for root
if [[ $EUID -ne 0 ]]; then
    echo "This installer needs root privileges to copy files to system directories."
    echo "Re-run with: sudo $0"
    exit 1
fi

# 1. Install binary
echo "[1/5] Installing binary to /usr/local/bin/..."
cp "$DIST_DIR/ghelper-linux" /usr/local/bin/
chmod +x /usr/local/bin/ghelper-linux

# Install shared libraries alongside binary
for lib in "$DIST_DIR"/lib*.so; do
    if [[ -f "$lib" ]]; then
        cp "$lib" /usr/local/lib/
    fi
done
ldconfig 2>/dev/null || true

# 2. Install udev rules
echo "[2/5] Installing udev rules..."
cp "$SCRIPT_DIR/90-ghelper.rules" /etc/udev/rules.d/
udevadm control --reload-rules 2>/dev/null || true
udevadm trigger 2>/dev/null || true

# 3. Install desktop entry
echo "[3/5] Installing desktop entry..."
cp "$SCRIPT_DIR/ghelper-linux.desktop" /usr/share/applications/

# 4. Install icon (extract from favicon.ico or use a png)
echo "[4/5] Installing icon..."
ICON_SRC="$PROJECT_DIR/src/UI/Assets/favicon.ico"
if [[ -f "$ICON_SRC" ]]; then
    mkdir -p /usr/share/icons/hicolor/64x64/apps
    # If ImageMagick is available, convert ico to png
    if command -v convert &>/dev/null; then
        convert "$ICON_SRC[0]" /usr/share/icons/hicolor/64x64/apps/ghelper-linux.png 2>/dev/null
    else
        # Just copy the ico as-is; most DEs can handle it
        cp "$ICON_SRC" /usr/share/icons/hicolor/64x64/apps/ghelper-linux.ico
        # Update desktop file to use .ico
        sed -i 's|Icon=ghelper-linux|Icon=/usr/share/icons/hicolor/64x64/apps/ghelper-linux.ico|' \
            /usr/share/applications/ghelper-linux.desktop
    fi
    gtk-update-icon-cache /usr/share/icons/hicolor 2>/dev/null || true
fi

# 5. Install autostart entry (optional, for current user)
echo "[5/5] Installing autostart entry for $(logname 2>/dev/null || echo 'current user')..."
REAL_USER=$(logname 2>/dev/null || echo "$SUDO_USER")
if [[ -n "$REAL_USER" ]]; then
    AUTOSTART_DIR="/home/$REAL_USER/.config/autostart"
    mkdir -p "$AUTOSTART_DIR"
    cp "$SCRIPT_DIR/ghelper-linux.desktop" "$AUTOSTART_DIR/"
    chown "$REAL_USER:$REAL_USER" "$AUTOSTART_DIR/ghelper-linux.desktop"
fi

echo ""
echo "=== Installation Complete ==="
echo ""
echo "What was installed:"
echo "  Binary:    /usr/local/bin/ghelper-linux"
echo "  udev:      /etc/udev/rules.d/90-ghelper.rules"
echo "  Desktop:   /usr/share/applications/ghelper-linux.desktop"
echo "  Autostart: ~/.config/autostart/ghelper-linux.desktop"
echo ""
echo "You may need to:"
echo "  1. Log out and back in for udev rules to fully apply"
echo "  2. Add your user to the 'input' group: sudo usermod -aG input \$USER"
echo ""
echo "Launch G-Helper:"
echo "  ghelper-linux"
