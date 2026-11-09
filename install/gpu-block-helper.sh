#!/bin/bash
# G-Helper GPU block helper.
# Manages GPU block artifacts (modprobe + udev) for Eco mode transitions.
# Called by ghelper via sudo (NOPASSWD via /etc/sudoers.d/zz-ghelper-gpu).
#
# Usage:
#   sudo gpu-block-helper.sh write <mode> [backend]
#   sudo gpu-block-helper.sh persist <mode>
#   sudo gpu-block-helper.sh unpersist
#   sudo gpu-block-helper.sh set-backend <backend>
#   sudo gpu-block-helper.sh live-standard
#   sudo gpu-block-helper.sh clear-recovery
#   sudo gpu-block-helper.sh clean
#   sudo gpu-block-helper.sh uninstall
#
# Backends:
#   asus-wmi (default) - boot script writes dgpu_disable=1 via firmware.
#                        Block files are temporary (removed after apply).
#   ebpf               - eBPF LSM filter hides the dGPU; boot re-attaches it.
#   pci                - modprobe block + udev rule are the persistent Eco
#                        state. Boot script never touches firmware sysfs.
set -euo pipefail

PATH=/usr/sbin:/usr/bin:/sbin:/bin
export PATH

MODPROBE_DEST="/etc/modprobe.d/ghelper-gpu-block.conf"
UDEV_DEST="/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"
TRIGGER_DIR="/etc/ghelper"
TRIGGER_DEST="$TRIGGER_DIR/pending-gpu-mode"
PERSISTENT_DEST="$TRIGGER_DIR/persistent-gpu-mode"
BACKEND_DEST="$TRIGGER_DIR/backend"

case "${1:-}" in
    write)
        MODE="${2:-eco}"
        BACKEND="${3:-asus-wmi}"
        case "$MODE" in
            eco|standard|optimized|ultimate) ;;
            *)
                echo "Error: invalid mode '$MODE' (expected: eco|standard|optimized|ultimate)" >&2
                exit 1
                ;;
        esac
        case "$BACKEND" in
            asus-wmi|pci|ebpf) ;;
            *)
                echo "Error: invalid backend '$BACKEND' (expected: asus-wmi|pci|ebpf)" >&2
                exit 1
                ;;
        esac

        mkdir -p "$TRIGGER_DIR"
        # Backend marker, read by ghelper-gpu-boot.sh on every boot.
        # Persistent user preference, survives the clean subcommand.
        echo "$BACKEND" > "$BACKEND_DEST"
        chmod 644 "$BACKEND_DEST"

        # ebpf needs the dGPU on the bus: no block files, stale ones are dropped.
        if [[ "$MODE" == "eco" && "$BACKEND" != "ebpf" ]]; then
            # Modprobe block: strongest form, prevents loading by any means.
            cat > "$MODPROBE_DEST" << 'GHELPER_EOF'
# ghelper: block dGPU driver modules for Eco mode
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

            # Udev rule: remove dGPU PCI devices from bus on add.
            cat > "$UDEV_DEST" << 'GHELPER_EOF'
# ghelper: remove dGPU PCI devices so no driver can bind
# boot_vga guard: skip removal when dGPU is the sole display (MUX=0/Ultimate)
# NVIDIA VGA controller
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x030000", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
# NVIDIA 3D controller
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x030200", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
# NVIDIA Audio (HDMI audio on dGPU)
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x040300", ATTR{power/control}="auto", ATTR{remove}="1"
# NVIDIA USB xHCI Host Controller
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x0c0330", ATTR{power/control}="auto", ATTR{remove}="1"
# NVIDIA USB Type-C UCSI
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x0c8000", ATTR{power/control}="auto", ATTR{remove}="1"
# AMD dGPU VGA controller (boot_vga!=1 protects the iGPU)
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x1002", ATTR{class}=="0x030000", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
# AMD dGPU 3D controller
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x1002", ATTR{class}=="0x030200", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
# AMD dGPU Audio
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x1002", ATTR{class}=="0x040300", ATTR{power/control}="auto", ATTR{remove}="1"
GHELPER_EOF
            chmod 644 "$UDEV_DEST"
        else
            # Non-eco or ebpf: remove any block files.
            rm -f "$MODPROBE_DEST" "$UDEV_DEST"
        fi

        # Trigger file for the boot script.
        echo "$MODE" > "$TRIGGER_DEST"
        ;;
    persist)
        # Write persistent Eco marker. Unlike the one-shot trigger, this
        # survives cleanup so Eco is re-applied on every boot.
        # optimized is ebpf-only: the boot script picks the leg by AC state.
        MODE="${2:-eco}"
        case "$MODE" in
            eco) ;;
            optimized)
                if [[ "$(cat "$BACKEND_DEST" 2>/dev/null)" != "ebpf" ]]; then
                    echo "Error: persist 'optimized' needs the ebpf backend" >&2
                    exit 1
                fi
                ;;
            *)
                echo "Error: persist only supports 'eco' or 'optimized' (got '$MODE')" >&2
                exit 1
                ;;
        esac
        mkdir -p "$TRIGGER_DIR"
        echo "$MODE" > "$PERSISTENT_DEST"
        chmod 644 "$PERSISTENT_DEST"
        ;;
    unpersist)
        # Remove persistent Eco marker.
        rm -f "$PERSISTENT_DEST"
        ;;
    clean)
        # Remove ephemeral artifacts. Backend marker and persistent marker
        # are preserved (removed only by unpersist or uninstall).
        rm -f "$MODPROBE_DEST" "$UDEV_DEST" "$TRIGGER_DEST"
        ;;
    uninstall)
        # Full reset: remove everything including backend and persistent markers.
        rm -f "$MODPROBE_DEST" "$UDEV_DEST" "$TRIGGER_DEST" "$PERSISTENT_DEST" "$BACKEND_DEST" \
              "$TRIGGER_DIR/gpu-allowed-comms"
        ;;
    set-backend)
        # Update the backend marker. Called from the UI when the user toggles
        # PCI mode so the boot service sees it on the next boot.
        BACKEND="${2:-asus-wmi}"
        case "$BACKEND" in
            asus-wmi|pci|ebpf) ;;
            *)
                echo "Error: invalid backend '$BACKEND' (expected: asus-wmi|pci|ebpf)" >&2
                exit 1
                ;;
        esac
        mkdir -p "$TRIGGER_DIR"
        echo "$BACKEND" > "$BACKEND_DEST"
        chmod 644 "$BACKEND_DEST"
        ;;
    clear-recovery)
        # Drop the boot recovery marker; the app cannot unlink in root-owned /etc/ghelper.
        rm -f "$TRIGGER_DIR/last-recovery"
        ;;
    live-standard)
        # Live recovery from PCI Eco to Standard. Drops block files, reloads
        # udev, rescans PCI bus, and kicks modprobe so the dGPU driver binds.
        # No reboot required.
        rm -f "$MODPROBE_DEST" "$UDEV_DEST" "$TRIGGER_DEST" "$PERSISTENT_DEST"
        udevadm control --reload-rules 2>/dev/null || true
        echo 1 > /sys/bus/pci/rescan 2>/dev/null || true
        modprobe nvidia 2>/dev/null || true
        modprobe amdgpu 2>/dev/null || true
        ;;
    *)
        echo "Usage: $0 {write <mode> [backend]|persist <mode>|unpersist|set-backend <backend>|live-standard|clear-recovery|clean|uninstall}" >&2
        exit 1
        ;;
esac
