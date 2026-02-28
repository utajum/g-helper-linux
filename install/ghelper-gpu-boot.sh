#!/usr/bin/env bash
# ╔══════════════════════════════════════════════════════════════════════╗
# ║  G-HELPER GPU MODE BOOT APPLICATION                                  ║
# ║  Applied BEFORE display-manager.service by ghelper-gpu-boot.service. ║
# ║  Reads pending GPU mode from trigger file, applies sysfs writes,     ║
# ║  cleans up block artifacts.                                          ║
# ║                                                                      ║
# ║  Runs as root via systemd — no sudo/pkexec needed.                   ║
# ║  CRITICAL: This script MUST NOT block boot. Always exit 0.           ║
# ║  The ghelper app's ApplyPendingOnStartup() is the fallback.          ║
# ╚══════════════════════════════════════════════════════════════════════╝

# NO set -e — we must never abort on error. Individual errors are handled.
set -uo pipefail

LOG_TAG="ghelper-gpu-boot"

# ── Paths ──────────────────────────────────────────────────────────────────────
# Legacy asus-nb-wmi sysfs bases (tried in order, matching SysfsHelper.ResolveAttrPath)
LEGACY_BASES=(
    "/sys/bus/platform/devices/asus-nb-wmi"
    "/sys/devices/platform/asus-nb-wmi"
)
# Firmware-attributes (asus-armoury, kernel 6.8+)
FW_ATTR_BASE="/sys/class/firmware-attributes/asus-armoury/attributes"

TRIGGER="/etc/ghelper/pending-gpu-mode"
# Vendor-aware block file (blocks both nvidia + amdgpu)
MODPROBE_BLOCK="/etc/modprobe.d/ghelper-gpu-block.conf"
UDEV_BLOCK="/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"

log() { logger -t "$LOG_TAG" "$*"; echo "$LOG_TAG: $*"; }

# ── Resolve sysfs path (mirrors SysfsHelper.ResolveAttrPath) ─────────────────
# Usage: resolve_sysfs_path <attr_name>
# Tries legacy asus-nb-wmi paths first, then firmware-attributes (asus-armoury).
# Prints the first path that exists, or empty string if not found.
resolve_sysfs_path() {
    local attr="$1"
    # Try legacy paths first
    for base in "${LEGACY_BASES[@]}"; do
        local path="$base/$attr"
        if [[ -f "$path" ]]; then
            echo "$path"
            return
        fi
    done
    # Try firmware-attributes (asus-armoury)
    local fw_path="$FW_ATTR_BASE/$attr/current_value"
    if [[ -f "$fw_path" ]]; then
        echo "$fw_path"
        return
    fi
    # Not found
    echo ""
}

dgpu_path=$(resolve_sysfs_path "dgpu_disable")
mux_path=$(resolve_sysfs_path "gpu_mux_mode")

# Log which backend was resolved (helpful for diagnostics in journal)
if [[ -n "$dgpu_path" ]]; then
    log "dgpu_disable resolved: $dgpu_path"
else
    log "dgpu_disable: not found (no asus-nb-wmi or asus-armoury)"
fi
if [[ -n "$mux_path" ]]; then
    log "gpu_mux_mode resolved: $mux_path"
else
    log "gpu_mux_mode: not found (no asus-nb-wmi or asus-armoury)"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 1: Boot safety check (ALWAYS runs, even without trigger)
#
#  TWO impossible states when MUX=0 (dGPU is sole display):
#
#  1) MUX=0 + dgpu_disable=1 → dGPU powered off but is sole display → black screen
#     Fix: force dgpu_disable=0
#
#  2) MUX=0 + modprobe GPU block present → dGPU driver can't load, dGPU has no driver
#     → black screen even though dGPU is powered on
#     Fix: remove block artifacts so dGPU driver loads normally
#
#  BOTH must be cleaned. Even if dgpu_disable=0, a modprobe block with MUX=0 is fatal.
# ══════════════════════════════════════════════════════════════════════════════
if [[ -n "$mux_path" ]]; then
    mux_val=$(cat "$mux_path" 2>/dev/null || echo "-1")
    if [[ "$mux_val" == "0" ]]; then
        # MUX=0: dGPU is sole display — dGPU driver MUST load, block artifacts are FATAL
        recovery_needed=0

        # Fix impossible state 1: MUX=0 + dgpu_disable=1
        if [[ -n "$dgpu_path" ]]; then
            dgpu_val=$(cat "$dgpu_path" 2>/dev/null || echo "-1")
            if [[ "$dgpu_val" == "1" ]]; then
                log "SAFETY: MUX=0 + dgpu_disable=1 — IMPOSSIBLE STATE, forcing dgpu_disable=0"
                echo 0 > "$dgpu_path" 2>/dev/null || log "SAFETY: failed to write dgpu_disable=0"
                recovery_needed=1
            fi
        fi

        # Fix impossible state 2: MUX=0 + GPU block artifacts present
        if [[ -f "$MODPROBE_BLOCK" || -f "$UDEV_BLOCK" ]]; then
            log "SAFETY: MUX=0 + GPU block artifacts present — removing (dGPU driver must load for display)"
            rm -f "$MODPROBE_BLOCK" "$UDEV_BLOCK" 2>/dev/null
            udevadm control --reload-rules 2>/dev/null || true
            recovery_needed=1
        fi

        # If trigger says "eco", that's impossible with MUX=0 — discard it
        if [[ -f "$TRIGGER" ]]; then
            trig_mode=$(cat "$TRIGGER" 2>/dev/null | tr -d '[:space:]')
            if [[ "$trig_mode" == "eco" || "$trig_mode" == "1" ]]; then
                log "SAFETY: MUX=0 + trigger='$trig_mode' — discarding impossible Eco trigger"
                rm -f "$TRIGGER" 2>/dev/null
                recovery_needed=1
            fi
        fi

        # If any recovery was performed, force MUX=1 and write recovery marker
        if [[ "$recovery_needed" == "1" ]]; then
            # Force MUX=1 to recover from Ultimate to Standard (known-good baseline)
            log "SAFETY: forcing MUX=1 (Standard) — recovering from impossible state"
            echo 1 > "$mux_path" 2>/dev/null || log "SAFETY: failed to write MUX=1"

            # Write recovery marker so the app can notify the user
            mkdir -p /etc/ghelper
            echo "$(date -Iseconds) MUX=0 + eco artifacts detected, recovered to Standard" > /etc/ghelper/last-recovery
            chmod 666 /etc/ghelper/last-recovery 2>/dev/null || true
            log "SAFETY: recovery marker written to /etc/ghelper/last-recovery"
        fi

        log "SAFETY: MUX=0 safety check complete"
        # If we cleaned anything, exit — let the system boot into Standard normally
        if [[ ! -f "$TRIGGER" ]]; then
            exit 0
        fi
    fi
fi

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 2: Check for pending mode
# ══════════════════════════════════════════════════════════════════════════════
if [[ ! -f "$TRIGGER" ]]; then
    log "No pending mode — nothing to do"
    exit 0
fi

MODE=$(cat "$TRIGGER" 2>/dev/null || echo "")
MODE=$(echo "$MODE" | tr -d '[:space:]')  # Strip whitespace/newlines
log "Pending GPU mode: '$MODE'"

# Backward compatibility: "1" means "eco" (old trigger format)
if [[ "$MODE" == "1" ]]; then
    MODE="eco"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 3: Validate mode makes sense
# ══════════════════════════════════════════════════════════════════════════════
if [[ -z "$MODE" ]]; then
    log "Empty trigger file — cleaning up"
    rm -f "$TRIGGER" 2>/dev/null
    exit 0
fi

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 4: Apply mode
# ══════════════════════════════════════════════════════════════════════════════
case "$MODE" in
    eco)
        if [[ -z "$dgpu_path" ]]; then
            log "eco: dgpu_disable sysfs not found — skipping, app will handle"
        else
            # Check MUX — cannot set Eco when MUX=0
            if [[ -n "$mux_path" ]]; then
                mux_val=$(cat "$mux_path" 2>/dev/null || echo "1")
                if [[ "$mux_val" == "0" ]]; then
                    log "eco: MUX=0 (Ultimate) — cannot apply Eco, cleaning up"
                    rm -f "$MODPROBE_BLOCK" "$UDEV_BLOCK" "$TRIGGER" 2>/dev/null
                    exit 0
                fi
            fi

            current=$(cat "$dgpu_path" 2>/dev/null || echo "0")
            if [[ "$current" == "1" ]]; then
                log "eco: dgpu_disable already 1 — already in Eco"
            else
                # Check dGPU driver is NOT loaded (should be blocked by modprobe.d)
                if [[ -d /sys/module/nvidia_drm ]] || [[ -d /sys/module/nouveau ]] || [[ -d /sys/module/amdgpu ]]; then
                    log "eco: WARNING — dGPU driver is loaded (nvidia_drm, nouveau, or amdgpu), cannot safely write dgpu_disable=1"
                    log "eco: leaving trigger for ghelper app to handle"
                    exit 0
                fi
                # Write dgpu_disable=1
                log "eco: writing dgpu_disable=1"
                echo 1 > "$dgpu_path" 2>/dev/null
                # Verify readback
                actual=$(cat "$dgpu_path" 2>/dev/null || echo "0")
                if [[ "$actual" == "1" ]]; then
                    log "eco: dgpu_disable=1 confirmed"
                else
                    log "eco: WARNING — dgpu_disable readback=$actual (expected 1)"
                fi
            fi
        fi
        ;;

    standard|optimized)
        # Ensure dGPU is enabled
        if [[ -n "$dgpu_path" ]]; then
            current=$(cat "$dgpu_path" 2>/dev/null || echo "0")
            if [[ "$current" == "1" ]]; then
                log "$MODE: writing dgpu_disable=0 (enabling dGPU)"
                echo 0 > "$dgpu_path" 2>/dev/null
                # PCI bus rescan so dGPU reappears
                if [[ -f /sys/bus/pci/rescan ]]; then
                    sleep 0.05
                    echo 1 > /sys/bus/pci/rescan 2>/dev/null || true
                    log "$MODE: PCI bus rescan triggered"
                fi
            else
                log "$MODE: dgpu already enabled (dgpu_disable=0)"
            fi
        fi
        ;;

    ultimate)
        # dGPU must be enabled in Ultimate (MUX=0)
        if [[ -n "$dgpu_path" ]]; then
            current=$(cat "$dgpu_path" 2>/dev/null || echo "0")
            if [[ "$current" == "1" ]]; then
                log "ultimate: writing dgpu_disable=0 (enabling dGPU)"
                echo 0 > "$dgpu_path" 2>/dev/null
                if [[ -f /sys/bus/pci/rescan ]]; then
                    sleep 0.05
                    echo 1 > /sys/bus/pci/rescan 2>/dev/null || true
                fi
            else
                log "ultimate: dgpu already enabled"
            fi
        fi
        # MUX should already be latched (written before reboot by ghelper app)
        if [[ -n "$mux_path" ]]; then
            mux_val=$(cat "$mux_path" 2>/dev/null || echo "-1")
            log "ultimate: MUX=$mux_val (expected 0 if latch took effect)"
        fi
        ;;

    *)
        log "Unknown mode '$MODE' — cleaning up without hardware action"
        ;;
esac

# ══════════════════════════════════════════════════════════════════════════════
#  STEP 5: Clean up block artifacts
#  After applying the mode, the modprobe block and udev remove rules are no
#  longer needed. dGPU driver can load normally on next boot (unless ghelper
#  writes a new block).
# ══════════════════════════════════════════════════════════════════════════════
rm -f "$MODPROBE_BLOCK" "$UDEV_BLOCK" "$TRIGGER" 2>/dev/null

# Reload udev rules so the removed rule no longer triggers PCI device removal
if command -v udevadm &>/dev/null; then
    udevadm control --reload-rules 2>/dev/null || true
fi

log "Boot GPU mode application complete — block artifacts cleaned"
exit 0
