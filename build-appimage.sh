#!/usr/bin/env bash
set -euo pipefail

# G-Helper Linux — AppImage Builder
# Packages the dist/ output into a portable AppImage.
#
# Prerequisites:
#   ./build.sh          (produces dist/ghelper — single binary with embedded native libs)
#
# Usage:
#   ./build-appimage.sh
#
# Output:
#   GHelper-x86_64.AppImage

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$SCRIPT_DIR/dist"
INSTALL_DIR="$SCRIPT_DIR/install"
APPDIR="$SCRIPT_DIR/GHelper.AppDir"
OUTPUT="$SCRIPT_DIR/GHelper-x86_64.AppImage"
APPIMAGETOOL="$SCRIPT_DIR/appimagetool"
RUNTIME="$SCRIPT_DIR/appimage-runtime"

# Pinned build tools: bump URL and sha256 together
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
RUNTIME_URL="https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64"
RUNTIME_SHA256="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"

echo "=== G-Helper AppImage Build ==="
echo ""

# ── Verify dist/ exists ───────────────────────────────────────────────────────

if [[ ! -f "$DIST_DIR/ghelper" ]]; then
    echo "ERROR: dist/ghelper not found."
    echo "Run ./build.sh first to compile the binary."
    exit 1
fi

# ── Download appimagetool if needed ───────────────────────────────────────────

# Download $1 to $3 unless $3 already has sha256 $2
fetch_pinned() {
    [[ -f "$3" && "$(sha256sum "$3" | cut -d' ' -f1)" == "$2" ]] && return
    curl -fsSL "$1" -o "$3.tmp"
    if [[ "$(sha256sum "$3.tmp" | cut -d' ' -f1)" != "$2" ]]; then
        echo "ERROR: sha256 mismatch for $1"
        rm -f "$3.tmp"
        exit 1
    fi
    mv "$3.tmp" "$3"
}

echo "[1/4] Fetching appimagetool and runtime..."
fetch_pinned "$APPIMAGETOOL_URL" "$APPIMAGETOOL_SHA256" "$APPIMAGETOOL"
fetch_pinned "$RUNTIME_URL" "$RUNTIME_SHA256" "$RUNTIME"
chmod +x "$APPIMAGETOOL"

# ── Assemble AppDir ──────────────────────────────────────────────────────────

echo "[2/4] Assembling AppDir..."

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/lib"

# Binary only — native .so libs are embedded and extracted at runtime
cp "$DIST_DIR/ghelper"             "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/ghelper"

# Desktop entry (required by appimagetool at AppDir root + standard path for validation)
cp "$INSTALL_DIR/ghelper.desktop" "$APPDIR/ghelper.desktop"
mkdir -p "$APPDIR/usr/share/applications"
cp "$INSTALL_DIR/ghelper.desktop" "$APPDIR/usr/share/applications/"

# Icon (required by appimagetool at AppDir root, matching Icon= in .desktop)
if [[ -f "$INSTALL_DIR/ghelper.png" ]]; then
    cp "$INSTALL_DIR/ghelper.png" "$APPDIR/ghelper.png"
else
    echo "WARNING: install/ghelper.png not found. Generating from favicon.ico..."
    if command -v convert &>/dev/null; then
        convert "$SCRIPT_DIR/src/UI/Assets/favicon.ico[2]" "$APPDIR/ghelper.png"
    else
        echo "ERROR: ImageMagick not found and install/ghelper.png missing."
        echo "Install ImageMagick or create install/ghelper.png manually."
        exit 1
    fi
fi

# AppStream metadata (suppresses appimagetool warning)
mkdir -p "$APPDIR/usr/share/metainfo"
cp "$INSTALL_DIR/ghelper.appdata.xml" "$APPDIR/usr/share/metainfo/"

# AppRun — entry point that appimagetool expects
cat > "$APPDIR/AppRun" << 'APPRUN'
#!/bin/bash
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/ghelper" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# ── Build AppImage ────────────────────────────────────────────────────────────

echo "[3/4] Building AppImage..."

# ARCH must be set for appimagetool
export ARCH=x86_64

# appimagetool itself is an AppImage — if FUSE is not available, extract and run
if "$APPIMAGETOOL" --version &>/dev/null 2>&1; then
    "$APPIMAGETOOL" --no-appstream --runtime-file "$RUNTIME" "$APPDIR" "$OUTPUT"
else
    echo "  (FUSE not available, using --appimage-extract-and-run)"
    "$APPIMAGETOOL" --no-appstream --appimage-extract-and-run --runtime-file "$RUNTIME" "$APPDIR" "$OUTPUT"
fi

# ── Cleanup ───────────────────────────────────────────────────────────────────

echo "[4/4] Cleaning up..."
rm -rf "$APPDIR"

# ── Summary ───────────────────────────────────────────────────────────────────

APPIMAGE_SIZE=$(du -sh "$OUTPUT" | cut -f1)
APPIMAGE_NAME=$(basename "$OUTPUT")
echo ""
echo "=== AppImage Build Complete ==="
echo "  Output: $OUTPUT"
echo "  Size:   $APPIMAGE_SIZE"
echo ""
echo "Run it:"
echo "  chmod +x $APPIMAGE_NAME"
echo "  ./$APPIMAGE_NAME"
