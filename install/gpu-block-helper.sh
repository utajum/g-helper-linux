#!/bin/bash
# ╔══════════════════════════════════════════════════════════════════════╗
# ║  G-HELPER GPU BLOCK HELPER                                          ║
# ║  Manages GPU block artifacts (vendor-aware: nvidia + amdgpu)         ║
# ║  for Eco mode boot transitions.                                      ║
# ║  Called by ghelper via sudo (NOPASSWD via /etc/sudoers.d/ghelper).   ║
# ║                                                                      ║
# ║  Usage:                                                              ║
# ║    sudo /usr/local/lib/ghelper/gpu-block-helper.sh write SRC1 SRC2  ║
# ║    sudo /usr/local/lib/ghelper/gpu-block-helper.sh clean             ║
# ╚══════════════════════════════════════════════════════════════════════╝
set -euo pipefail

MODPROBE_DEST="/etc/modprobe.d/ghelper-gpu-block.conf"
UDEV_DEST="/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"
TRIGGER_DIR="/etc/ghelper"
TRIGGER_DEST="$TRIGGER_DIR/pending-gpu-mode"

case "${1:-}" in
    write)
        # Args: $2 = temp modprobe file, $3 = temp udev file, $4 = mode name (optional)
        if [[ -z "${2:-}" || -z "${3:-}" ]]; then
            echo "Usage: $0 write <modprobe-src> <udev-src> [mode]" >&2
            exit 1
        fi
        MODE="${4:-eco}"
        mkdir -p "$TRIGGER_DIR"
        install -m 644 "$2" "$MODPROBE_DEST"
        install -m 644 "$3" "$UDEV_DEST"
        echo "$MODE" > "$TRIGGER_DEST"
        ;;
    clean)
        rm -f "$MODPROBE_DEST" "$UDEV_DEST" "$TRIGGER_DEST"
        ;;
    *)
        echo "Usage: $0 {write|clean}" >&2
        exit 1
        ;;
esac
