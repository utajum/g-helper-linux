#!/bin/bash
# ╔══════════════════════════════════════════════════════════════════════╗
# ║  G-HELPER GPU BLOCK HELPER                                          ║
# ║  Manages GPU block artifacts (vendor-aware: nvidia + amdgpu)         ║
# ║  for Eco mode boot transitions.                                      ║
# ║  Called by ghelper via sudo (NOPASSWD via /etc/sudoers.d/ghelper).   ║
# ║                                                                      ║
# ║  Usage:                                                              ║
# ║    sudo /usr/local/lib/ghelper/gpu-block-helper.sh write [mode]      ║
# ║    sudo /usr/local/lib/ghelper/gpu-block-helper.sh clean             ║
# ╚══════════════════════════════════════════════════════════════════════╝
set -euo pipefail

MODPROBE_DEST="/etc/modprobe.d/ghelper-gpu-block.conf"
UDEV_DEST="/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"
TRIGGER_DIR="/etc/ghelper"
TRIGGER_DEST="$TRIGGER_DIR/pending-gpu-mode"

case "${1:-}" in
    write)
        MODE="${2:-eco}"
        # Validate mode — only known values accepted
        case "$MODE" in
            eco|standard|optimized|ultimate) ;;
            *)
                echo "Error: invalid mode '$MODE' (expected: eco|standard|optimized|ultimate)" >&2
                exit 1
                ;;
        esac

        mkdir -p "$TRIGGER_DIR"

        # Modprobe block — prevent dGPU driver loading
        cat > "$MODPROBE_DEST" << 'GHELPER_EOF'
# ghelper: block dGPU driver modules so dGPU can be safely disabled on next boot
# Auto-generated — will be removed after Eco mode is applied
# Uses 'install /bin/false' (strongest block — prevents loading by ANY means)
# NVIDIA modules
install nvidia /bin/false
install nvidia_drm /bin/false
install nvidia_modeset /bin/false
install nvidia_uvm /bin/false
install nvidia_wmi_ec_backlight /bin/false
# Open-source NVIDIA driver
install nouveau /bin/false
# AMD dGPU driver
install amdgpu /bin/false
GHELPER_EOF
        chmod 644 "$MODPROBE_DEST"

        # Udev rule — remove dGPU PCI devices from bus on add
        cat > "$UDEV_DEST" << 'GHELPER_EOF'
# ghelper: remove dGPU PCI devices so no driver can bind
# Auto-generated — will be removed after Eco mode is applied
# Remove NVIDIA VGA controller
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x030000", ATTR{power/control}="auto", ATTR{remove}="1"
# Remove NVIDIA 3D controller
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x030200", ATTR{power/control}="auto", ATTR{remove}="1"
# Remove NVIDIA Audio devices (HDMI audio on dGPU)
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x040300", ATTR{power/control}="auto", ATTR{remove}="1"
# Remove NVIDIA USB xHCI Host Controller
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x0c0330", ATTR{power/control}="auto", ATTR{remove}="1"
# Remove NVIDIA USB Type-C UCSI devices
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x0c8000", ATTR{power/control}="auto", ATTR{remove}="1"
# Remove AMD dGPU VGA controller (boot_vga!=1 protects the iGPU)
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x1002", ATTR{class}=="0x030000", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
# Remove AMD dGPU 3D controller
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x1002", ATTR{class}=="0x030200", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
# Remove AMD dGPU Audio devices
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x1002", ATTR{class}=="0x040300", ATTR{power/control}="auto", ATTR{remove}="1"
GHELPER_EOF
        chmod 644 "$UDEV_DEST"

        # Trigger file — tells ghelper on startup which mode to apply
        echo "$MODE" > "$TRIGGER_DEST"
        ;;
    clean)
        rm -f "$MODPROBE_DEST" "$UDEV_DEST" "$TRIGGER_DEST"
        ;;
    *)
        echo "Usage: $0 {write [mode]|clean}" >&2
        exit 1
        ;;
esac
