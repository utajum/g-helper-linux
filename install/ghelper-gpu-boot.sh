#!/usr/bin/env bash
# G-Helper GPU mode boot script.
# Runs as root via ghelper-gpu-boot.service before the display manager.
# Reads the pending GPU mode from a trigger file, applies sysfs writes,
# and cleans up block artifacts.
#
# Set GHELPER_TEST_ROOT to redirect all paths into a sandbox for testing.
# See install/tests/test-ghelper-gpu-boot.sh for the test harness.
#
# Must never block boot. Always exits 0.

# No set -e: individual errors are handled.
set -uo pipefail

LOG_TAG="ghelper-gpu-boot"

# Sandbox root (empty in production, set to a temp dir in tests).
ROOT="${GHELPER_TEST_ROOT:-}"

# Paths (all prefixed with $ROOT for testability).
# Legacy asus-nb-wmi sysfs bases, tried in order.
LEGACY_BASES=(
    "${ROOT}/sys/bus/platform/devices/asus-nb-wmi"
    "${ROOT}/sys/devices/platform/asus-nb-wmi"
)
# Firmware-attributes (asus-armoury, kernel 6.8+).
FW_ATTR_BASE="${ROOT}/sys/class/firmware-attributes/asus-armoury/attributes"

TRIGGER="${ROOT}/etc/ghelper/pending-gpu-mode"
# Persistent Eco marker. Survives cleanup (unlike the one-shot trigger).
# When present and no one-shot trigger exists, its content is used as the
# pending mode, so Eco survives reboots on firmware that forgets dgpu_disable.
PERSISTENT_TRIGGER="${ROOT}/etc/ghelper/persistent-gpu-mode"
RETRY_COUNTER="${ROOT}/etc/ghelper/eco-retry-count"
FAILURE_MARKER="${ROOT}/etc/ghelper/last-eco-failed"
# Backend selector. "asus-wmi" (default) writes dgpu_disable via firmware.
# "pci" uses the modprobe block + udev rule as the persistent Eco state.
BACKEND_FILE="${ROOT}/etc/ghelper/backend"
MODPROBE_BLOCK="${ROOT}/etc/modprobe.d/ghelper-gpu-block.conf"
UDEV_BLOCK="${ROOT}/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"

PCI_DEVICES="${ROOT}/sys/bus/pci/devices"
PCI_RESCAN="${ROOT}/sys/bus/pci/rescan"
DEBUGFS_BASE="${ROOT}/sys/kernel/debug/asus-nb-wmi"

# Max consecutive failed Eco attempts before giving up.
MAX_ECO_RETRIES=3

# Log to journal and stdout (tests capture stdout).
log() { logger -t "$LOG_TAG" "$*" 2>/dev/null || true; echo "$LOG_TAG: $*"; }

# Side-effect wrappers (no-op in test mode).

udevadm_settle() {
    if [[ -n "$ROOT" ]]; then
        log "test: would udevadm settle"
        return 0
    fi
    if command -v udevadm &>/dev/null; then
        udevadm settle --timeout=10 2>/dev/null || true
    fi
}

udevadm_reload() {
    if [[ -n "$ROOT" ]]; then
        log "test: would udevadm control --reload-rules"
        return 0
    fi
    if command -v udevadm &>/dev/null; then
        udevadm control --reload-rules 2>/dev/null || true
    fi
}

pci_rescan() {
    if [[ ! -f "$PCI_RESCAN" ]]; then
        return 0
    fi
    echo 1 > "$PCI_RESCAN" 2>/dev/null || true
}

# Path to the privileged gpu-helper binary (whitelists rmmod/unbind/smi).
GPU_HELPER="${ROOT:+$ROOT}/opt/ghelper/gpu-helper"

# Run gpu-helper with a timeout. Args: <timeout_sec> <subcommand> [args...]
# Returns the gpu-helper exit code, or 124 on timeout.
run_gpu_helper() {
    local secs="$1"; shift
    if [[ -n "$ROOT" ]]; then
        log "test: would gpu-helper $*"
        return 0
    fi
    if [[ ! -x "$GPU_HELPER" ]]; then
        log "gpu-helper not found at $GPU_HELPER"
        return 1
    fi
    timeout "$secs" "$GPU_HELPER" "$@" 2>/dev/null
}

# Try to release nvidia so dgpu_disable can be written safely.
# Mirrors the app's "Switch Now" flow (GpuModeController.TryReleaseNvidiaDriver):
#   1. Reset GPU clocks (prevents D3cold stall on ACPI power-off)
#   2. Stop nvidia daemons (release /dev/nvidia* handles)
#   3. PCI unbind dGPU functions in reverse order (drops kernel refcounts)
#   4. rmmod nvidia modules
# Returns 0 if the driver was fully released, 1 if it is still bound.
try_release_nvidia() {
    # Test sandbox: simulate the release by removing driver symlinks and
    # module dirs (mirrors what the real gpu-helper unbind+rmmod would do).
    # Set test-release-fails to simulate a stuck driver that can't be released.
    if [[ -n "$ROOT" ]]; then
        if [[ -f "${ROOT}/test-release-fails" ]]; then
            log "test: try_release_nvidia - simulated failure (driver stays bound)"
            return 1
        fi
        log "test: try_release_nvidia - simulated success"
        # Remove driver symlinks from dGPU PCI devices.
        local d
        for d in "$PCI_DEVICES"/0000:01:00.*; do
            [[ -e "$d" ]] || continue
            rm -f "$d/driver" 2>/dev/null
        done
        # Remove nvidia module dirs.
        for d in nvidia_drm nvidia_modeset nvidia_uvm nvidia nvidia_wmi_ec_backlight; do
            rm -rf "${ROOT}/sys/module/$d" 2>/dev/null
        done
        return 0
    fi

    # Step 1: Reset GPU clocks. Locked clocks pin power management on and
    # cause the ACPI _PS3 path to stall ~25s during unbind/dgpu_disable.
    log "eco: resetting GPU clocks before driver release"
    run_gpu_helper 5 smi -rgc || true
    run_gpu_helper 5 smi -rmc || true

    # Step 2: Stop nvidia daemons. They hold /dev/nvidia* FDs which pin
    # module refcounts. At boot they may not be running yet (our service
    # runs Before=multi-user.target), but stop them defensively.
    systemctl stop nvidia-powerd.service 2>/dev/null || true
    systemctl stop nvidia-persistenced.service 2>/dev/null || true
    sleep 0.5

    # Step 3: PCI unbind dGPU functions in reverse order (audio before
    # graphics). This drops the kernel-internal refcounts that prevent
    # rmmod from succeeding.
    local dev bdf drv fns=()
    for dev in "$PCI_DEVICES"/0000:01:00.*; do
        [[ -e "$dev" ]] || continue
        fns+=("$dev")
    done

    local i
    for (( i=${#fns[@]}-1; i>=0; i-- )); do
        dev="${fns[$i]}"
        bdf=$(basename "$dev")
        if [[ -L "$dev/driver" ]]; then
            drv=$(basename "$(readlink -f "$dev/driver")" 2>/dev/null)
            log "eco: unbinding $drv from $bdf"
            run_gpu_helper 10 pci-unbind "$drv" "$bdf" || log "eco: unbind $bdf failed"
        fi
    done
    sleep 0.1

    # Step 4: rmmod nvidia modules. Each attempt bounded by timeout, up
    # to 3 retries per module with 200ms gap.
    local mod attempt
    for mod in nvidia_drm nvidia_modeset nvidia_uvm nvidia nvidia_wmi_ec_backlight; do
        for attempt in 1 2 3; do
            [[ -d "/sys/module/$mod" ]] || break
            run_gpu_helper 5 rmmod "$mod" || true
            sleep 0.2
        done
    done

    # Verify: is the driver still bound?
    if dgpu_driver_bound; then
        log "eco: driver still bound after release attempt"
        return 1
    fi
    log "eco: driver released successfully"
    return 0
}

# Check if the dGPU has nvidia or nouveau bound.
# Returns 0 if bound. Writing dgpu_disable or removing the device while
# a driver is bound deadlocks the kernel in an uninterruptible D-state,
# so we must never initiate teardown when this returns true.
dgpu_driver_bound() {
    [[ -d "$PCI_DEVICES" ]] || return 1
    local dev vendor boot_vga class drv
    for dev in "$PCI_DEVICES"/*; do
        [[ -e "$dev" ]] || continue
        vendor=$(cat "$dev/vendor" 2>/dev/null || echo "")
        case "$vendor" in
            0x10de) ;;
            0x1002)
                boot_vga=$(cat "$dev/boot_vga" 2>/dev/null || echo "0")
                [[ "$boot_vga" != "1" ]] || continue
                ;;
            *) continue ;;
        esac
        class=$(cat "$dev/class" 2>/dev/null || echo "")
        case "$class" in
            0x0300*|0x0302*) ;;
            *) continue ;;
        esac
        [[ -L "$dev/driver" ]] || continue
        # Only nvidia/nouveau cause the deadlock. Foreign drivers like
        # vfio-pci are handled separately by dgpu_foreign_driver.
        drv=$(basename "$(readlink -f "$dev/driver")" 2>/dev/null)
        case "$drv" in
            nvidia|nouveau) return 0 ;;
        esac
    done
    return 1
}

# Install modprobe block + udev hot-remove rule so nvidia can never load
# on the next boot. The udev rule removes the dGPU PCI device on add
# before any driver binds (a driverless remove is instant and safe).
install_block_artifacts() {
    if [[ -n "$ROOT" ]]; then
        printf 'blocked\n' > "$MODPROBE_BLOCK" 2>/dev/null || true
        printf 'rules\n'   > "$UDEV_BLOCK" 2>/dev/null || true
        log "test: installed block artifacts (modprobe + udev)"
        return 0
    fi
    mkdir -p "$(dirname "$MODPROBE_BLOCK")" "$(dirname "$UDEV_BLOCK")" 2>/dev/null || true
    # amdgpu is not blocked: it also drives AMD iGPUs, so AMD dGPUs are
    # left to the boot_vga-guarded udev rule below (#180).
    cat > "$MODPROBE_BLOCK" 2>/dev/null << 'GHELPER_EOF' || true
# ghelper: block dGPU driver modules for Eco mode
install nvidia /bin/false
install nvidia_drm /bin/false
install nvidia_modeset /bin/false
install nvidia_uvm /bin/false
install nvidia_wmi_ec_backlight /bin/false
install nouveau /bin/false
GHELPER_EOF
    chmod 644 "$MODPROBE_BLOCK" 2>/dev/null || true
    cat > "$UDEV_BLOCK" 2>/dev/null << 'GHELPER_EOF' || true
# ghelper: remove dGPU PCI devices so no driver can bind
# boot_vga guard: skip removal when dGPU is the sole display (MUX=0/Ultimate)
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x030000", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x10de", ATTR{class}=="0x030200", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x1002", ATTR{class}=="0x030000", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
ACTION=="add", SUBSYSTEM=="pci", ATTR{vendor}=="0x1002", ATTR{class}=="0x030200", ATTR{boot_vga}!="1", ATTR{power/control}="auto", ATTR{remove}="1"
GHELPER_EOF
    chmod 644 "$UDEV_BLOCK" 2>/dev/null || true
    udevadm_reload
    log "eco: installed persistent block artifacts (modprobe + udev)"
}

# Read dgpu_disable. In test mode a counter file can simulate readback
# failures so the double-write retry logic can be exercised.
read_dgpu_disable() {
    local path="$1"
    if [[ -n "$ROOT" ]]; then
        local fail_file="${ROOT}/test-dgpu-readback-fail-count"
        if [[ -f "$fail_file" ]]; then
            local n
            n=$(cat "$fail_file" 2>/dev/null || echo "0")
            if (( n > 0 )); then
                echo "$((n - 1))" > "$fail_file"
                echo "0"
                return 0
            fi
        fi
    fi
    cat "$path" 2>/dev/null || echo "0"
}

# Resolve sysfs path. Tries legacy asus-nb-wmi then firmware-attributes.
resolve_sysfs_path() {
    local attr="$1"
    for base in "${LEGACY_BASES[@]}"; do
        local path="$base/$attr"
        if [[ -f "$path" ]]; then
            echo "$path"
            return
        fi
    done
    local fw_path="$FW_ATTR_BASE/$attr/current_value"
    if [[ -f "$fw_path" ]]; then
        echo "$fw_path"
        return
    fi
    echo ""
}

# Check if amdgpu is bound to a dGPU (vendor 0x1002, boot_vga!=1).
# Returns 1 if amdgpu only drives the iGPU (boot_vga=1) or is absent.
amdgpu_drives_dgpu() {
    [[ -d "$PCI_DEVICES" ]] || return 1
    local dev vendor boot_vga drv_link drv_name
    for dev in "$PCI_DEVICES"/*; do
        [[ -e "$dev" ]] || continue
        drv_link="$dev/driver"
        [[ -L "$drv_link" ]] || continue
        drv_name=$(basename "$(readlink -f "$drv_link")")
        [[ "$drv_name" == "amdgpu" ]] || continue
        vendor=$(cat "$dev/vendor" 2>/dev/null || echo "")
        [[ "$vendor" == "0x1002" ]] || continue
        boot_vga=$(cat "$dev/boot_vga" 2>/dev/null || echo "0")
        if [[ "$boot_vga" != "1" ]]; then
            return 0
        fi
    done
    return 1
}

# Check for foreign drivers (vfio-pci, etc.) on the dGPU.
# Returns 0 and echoes the driver name if found.
dgpu_foreign_driver() {
    [[ -d "$PCI_DEVICES" ]] || return 1
    local dev vendor boot_vga class drv_link drv_name
    for dev in "$PCI_DEVICES"/*; do
        [[ -e "$dev" ]] || continue
        vendor=$(cat "$dev/vendor" 2>/dev/null || echo "")
        case "$vendor" in
            0x10de) ;;
            0x1002)
                boot_vga=$(cat "$dev/boot_vga" 2>/dev/null || echo "0")
                [[ "$boot_vga" != "1" ]] || continue
                ;;
            *) continue ;;
        esac
        class=$(cat "$dev/class" 2>/dev/null || echo "")
        case "$class" in
            0x0300*|0x0302*) ;;
            *) continue ;;
        esac
        drv_link="$dev/driver"
        [[ -L "$drv_link" ]] || continue
        drv_name=$(basename "$(readlink -f "$drv_link")")
        case "$drv_name" in
            nvidia|nouveau|amdgpu) continue ;;
        esac
        echo "$drv_name"
        return 0
    done
    return 1
}

# Re-apply suspend variant. The kernel resets /sys/power/mem_sleep to the
# firmware default on every boot, so the user's choice (written by ghelper
# to /etc/ghelper/mem-sleep) must be restored here. Runs before the GPU
# logic and its early exits - the two are independent.
apply_mem_sleep() {
    local state="${ROOT}/etc/ghelper/mem-sleep"
    local sys="${ROOT}/sys/power/mem_sleep"
    [[ -f "$state" && -f "$sys" ]] || return 0

    local v
    v="$(tr -d '[:space:]' < "$state")"
    [[ "$v" =~ ^[a-z0-9]+$ ]] || { log "mem_sleep: invalid value in $state"; return 0; }

    # Only write variants the kernel actually offers.
    if grep -qw "$v" "$sys" 2>/dev/null; then
        if echo "$v" > "$sys" 2>/dev/null; then
            log "mem_sleep restored: $v"
        else
            log "mem_sleep: write of '$v' failed"
        fi
    else
        log "mem_sleep: '$v' not offered by kernel, skipping"
    fi
}
apply_mem_sleep

# Older block files also blocked amdgpu, which breaks AMD iGPUs (#180).
# Strip that line from any file left on disk (persistent Eco and the PCI
# backend keep the file across boots).
if [[ -f "$MODPROBE_BLOCK" ]] && grep -q '^install amdgpu ' "$MODPROBE_BLOCK" 2>/dev/null; then
    sed -i '/^install amdgpu /d' "$MODPROBE_BLOCK" 2>/dev/null && log "removed stale amdgpu block from $MODPROBE_BLOCK"
    # udev coldplug already asked for amdgpu and was refused; load it now so
    # the iGPU has its driver this boot, not the next one.
    if [[ -z "$ROOT" ]] && [[ ! -d /sys/module/amdgpu ]]; then
        modprobe amdgpu 2>/dev/null && log "loaded amdgpu for the iGPU" || log "modprobe amdgpu failed"
    fi
fi

# Resolve hardware paths.
dgpu_path=$(resolve_sysfs_path "dgpu_disable")
mux_path=$(resolve_sysfs_path "gpu_mux_mode")

# Resolve backend selector. Unknown/missing defaults to asus-wmi.
BACKEND="asus-wmi"
if [[ -f "$BACKEND_FILE" ]]; then
    raw_backend=$(cat "$BACKEND_FILE" 2>/dev/null | tr -d '[:space:]')
    case "$raw_backend" in
        pci)       BACKEND="pci" ;;
        asus-wmi)  BACKEND="asus-wmi" ;;
        *)         log "backend: unknown value '$raw_backend' - falling back to asus-wmi" ;;
    esac
fi
log "backend: $BACKEND"

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

# STEP 1: MUX=0 boot safety check (runs every boot, even without trigger).
#
# Two impossible states when MUX=0 (dGPU is sole display):
#   1) MUX=0 + dgpu_disable=1 - dGPU off but is sole display = black screen
#   2) MUX=0 + modprobe block - dGPU driver can't load = black screen
# Fix: force dgpu_disable=0, remove blocks, force MUX=1 (Standard).
if [[ -n "$mux_path" ]]; then
    mux_val=$(cat "$mux_path" 2>/dev/null || echo "-1")
    if [[ "$mux_val" == "0" ]]; then
        recovery_needed=0

        # Fix state 1: MUX=0 + dgpu_disable=1
        if [[ -n "$dgpu_path" ]]; then
            dgpu_val=$(cat "$dgpu_path" 2>/dev/null || echo "-1")
            if [[ "$dgpu_val" == "1" ]]; then
                log "SAFETY: MUX=0 + dgpu_disable=1 - IMPOSSIBLE STATE, forcing dgpu_disable=0"
                echo 0 > "$dgpu_path" 2>/dev/null || log "SAFETY: failed to write dgpu_disable=0"
                recovery_needed=1
            fi
        fi

        # Fix state 2: MUX=0 + block artifacts
        if [[ -f "$MODPROBE_BLOCK" || -f "$UDEV_BLOCK" ]]; then
            log "SAFETY: MUX=0 + GPU block artifacts present - removing (dGPU driver must load for display)"
            rm -f "$MODPROBE_BLOCK" "$UDEV_BLOCK" 2>/dev/null
            udevadm_reload
            recovery_needed=1
        fi

        # Discard eco triggers (impossible with MUX=0)
        if [[ -f "$TRIGGER" ]]; then
            trig_mode=$(cat "$TRIGGER" 2>/dev/null | tr -d '[:space:]')
            if [[ "$trig_mode" == "eco" || "$trig_mode" == "1" ]]; then
                log "SAFETY: MUX=0 + trigger='$trig_mode' - discarding impossible Eco trigger"
                rm -f "$TRIGGER" 2>/dev/null
                recovery_needed=1
            fi
        fi

        if [[ -f "$PERSISTENT_TRIGGER" ]]; then
            ptrig_mode=$(cat "$PERSISTENT_TRIGGER" 2>/dev/null | tr -d '[:space:]')
            if [[ "$ptrig_mode" == "eco" || "$ptrig_mode" == "1" ]]; then
                log "SAFETY: MUX=0 + persistent='$ptrig_mode' - discarding impossible persistent Eco"
                rm -f "$PERSISTENT_TRIGGER" 2>/dev/null
                recovery_needed=1
            fi
        fi

        if [[ "$recovery_needed" == "1" ]]; then
            log "SAFETY: backend=$BACKEND recovery - forcing MUX=1 (Standard), preventing black screen"
            echo 1 > "$mux_path" 2>/dev/null || log "SAFETY: failed to write MUX=1"

            mkdir -p "${ROOT}/etc/ghelper"
            echo "$(date -Iseconds) backend=$BACKEND MUX=0 + eco artifacts detected, recovered to Standard" > "${ROOT}/etc/ghelper/last-recovery"
            chmod 666 "${ROOT}/etc/ghelper/last-recovery" 2>/dev/null || true
            log "SAFETY: recovery marker written to ${ROOT}/etc/ghelper/last-recovery"
        fi

        log "SAFETY: MUX=0 safety check complete"
        if [[ ! -f "$TRIGGER" ]] && [[ ! -f "$PERSISTENT_TRIGGER" ]]; then
            exit 0
        fi
    fi
fi

# STEP 2: Read pending mode (one-shot trigger, then persistent fallback).
# IS_PERSISTENT tracks which trigger we are using so STEP 5 knows whether
# to preserve the persistent marker.
IS_PERSISTENT=false

if [[ -f "$TRIGGER" ]]; then
    MODE=$(cat "$TRIGGER" 2>/dev/null || echo "")
    MODE=$(echo "$MODE" | tr -d '[:space:]')
    log "Pending GPU mode: '$MODE'"
elif [[ -f "$PERSISTENT_TRIGGER" ]]; then
    MODE=$(cat "$PERSISTENT_TRIGGER" 2>/dev/null || echo "")
    MODE=$(echo "$MODE" | tr -d '[:space:]')
    IS_PERSISTENT=true
    log "Persistent GPU mode: '$MODE'"
else
    log "No pending mode - nothing to do"
    exit 0
fi

if [[ "$MODE" == "1" ]]; then
    MODE="eco"
fi

# STEP 3: Validate mode.
if [[ -z "$MODE" ]]; then
    log "Empty trigger file - cleaning up"
    rm -f "$TRIGGER" 2>/dev/null
    exit 0
fi

# Track consecutive failed Eco attempts so we don't loop forever.
record_eco_failure() {
    local reason="$1"
    mkdir -p "${ROOT}/etc/ghelper"
    local count=0
    if [[ -f "$RETRY_COUNTER" ]]; then
        count=$(cat "$RETRY_COUNTER" 2>/dev/null || echo "0")
    fi
    # Sanitize non-numeric content (corrupted counter file).
    if [[ ! "$count" =~ ^[0-9]+$ ]]; then
        count=0
    fi
    count=$((count + 1))
    echo "$count" > "$RETRY_COUNTER"
    log "eco: failure #$count: $reason"

    if (( count >= MAX_ECO_RETRIES )); then
        log "eco: reached $MAX_ECO_RETRIES failed attempts - giving up, removing trigger"
        echo "$(date -Iseconds) Eco apply failed $count times: $reason" > "$FAILURE_MARKER"
        chmod 666 "$FAILURE_MARKER" 2>/dev/null || true
        rm -f "$TRIGGER" "$RETRY_COUNTER" 2>/dev/null
    fi
}

reset_eco_retry_counter() {
    rm -f "$RETRY_COUNTER" "$FAILURE_MARKER" 2>/dev/null
}

# Drain pending udev events before touching hardware.
udevadm_settle

# STEP 4: Apply mode.
case "$MODE" in
    eco)
        # Driver state check. nvidia may already be loaded by udev coldplug.
        # We never attempt rmmod/unbind/remove of a bound driver because
        # writing to the driver's sysfs or ACPI power path while bound
        # deadlocks the kernel in an uninterruptible D-state.
        #
        # If the driver is bound, install a modprobe+udev block and defer.
        # The block prevents the driver from loading on the next boot, so
        # the next boot comes up driverless and dgpu_disable succeeds.

        # Pure-AMD hybrid: can't unload amdgpu since iGPU may share it.
        if [[ -d "${ROOT}/sys/module/amdgpu" ]] && amdgpu_drives_dgpu; then
            record_eco_failure "amdgpu drives dGPU (pure-AMD hybrid), cannot unload safely"
            exit 0
        fi

        # Bound nvidia/nouveau: try to release the driver (mirrors the app's
        # "Switch Now" flow). If release succeeds, proceed to dgpu_disable.
        # If it fails, install a block so the next boot comes up driverless.
        if dgpu_driver_bound; then
            log "eco: dGPU driver is bound (nvidia/nouveau loaded by udev coldplug)"
            log "eco: attempting driver release (reset clocks, unbind, rmmod)"
            if try_release_nvidia; then
                log "eco: driver released - proceeding to dgpu_disable"
            else
                log "eco: release failed, installing block for next boot, deferring"
                record_eco_failure "dGPU driver still bound after release attempt"
                install_block_artifacts
                exit 0
            fi
        fi
        log "eco: dGPU is driverless - safe to apply"

        # Foreign driver (vfio-pci, etc.) bound to dGPU: can't disable
        # without yanking the device from a running VM.
        if foreign_drv=$(dgpu_foreign_driver); then
            record_eco_failure "dGPU bound to foreign driver '$foreign_drv' (passthrough?), refusing to disable"
            exit 0
        fi

        # PCI backend: block files are the persistent state, no firmware write.
        if [[ "$BACKEND" == "pci" ]]; then
            log "eco: PCI backend - blocks remain as persistent state, skipping dgpu_disable write"
            rm -f "$TRIGGER" 2>/dev/null
            reset_eco_retry_counter
            log "eco: PCI backend apply complete"
            exit 0
        fi

        if [[ -z "$dgpu_path" ]]; then
            # No sysfs, try debugfs raw WMI.
            if [[ -d "$DEBUGFS_BASE" ]]; then
                DEVID="0x00090020"
                echo "$DEVID" > "$DEBUGFS_BASE/dev_id" 2>/dev/null
                probe=$(cat "$DEBUGFS_BASE/dsts" 2>&1)
                if echo "$probe" | grep -q "No such device"; then
                    DEVID="0x00090120"
                    log "eco: ROG endpoint not supported, trying Vivobook ($DEVID)"
                fi
                log "eco: no sysfs, using debugfs raw WMI (DEVS $DEVID, 1)"
                # Double-write pattern per kernel dgpu_disable_store comment.
                echo "$DEVID" > "$DEBUGFS_BASE/dev_id" 2>/dev/null
                echo 1 > "$DEBUGFS_BASE/ctrl_param" 2>/dev/null
                result=$(cat "$DEBUGFS_BASE/devs" 2>&1)
                log "eco: raw WMI first write: $result"
                sleep 0.1
                pci_rescan
                sleep 0.1
                echo "$DEVID" > "$DEBUGFS_BASE/dev_id" 2>/dev/null
                echo 1 > "$DEBUGFS_BASE/ctrl_param" 2>/dev/null
                result=$(cat "$DEBUGFS_BASE/devs" 2>&1)
                log "eco: raw WMI second write: $result"
                reset_eco_retry_counter
            else
                record_eco_failure "no sysfs or debugfs node available"
            fi
        else
            # Cannot set Eco when MUX=0.
            if [[ -n "$mux_path" ]]; then
                mux_val=$(cat "$mux_path" 2>/dev/null || echo "1")
                if [[ "$mux_val" == "0" ]]; then
                    log "eco: MUX=0 (Ultimate) - cannot apply Eco, cleaning up"
                    rm -f "$MODPROBE_BLOCK" "$UDEV_BLOCK" "$TRIGGER" 2>/dev/null
                    reset_eco_retry_counter
                    exit 0
                fi
            fi

            current=$(read_dgpu_disable "$dgpu_path")
            if [[ "$current" == "1" ]]; then
                log "eco: dgpu_disable already 1 - already in Eco"
                reset_eco_retry_counter
            else
                # Double-write+rescan retry. Some firmware returns EIO on the
                # first write. Each write is bounded by a 10s timeout so a
                # hung write gets a clean log message instead of a SIGTERM.
                log "eco: writing dgpu_disable=1 (attempt 1)"
                if [[ -n "$ROOT" ]]; then
                    echo 1 > "$dgpu_path" 2>/dev/null
                else
                    timeout 10 bash -c "echo 1 > '$dgpu_path'" 2>/dev/null
                    if [[ $? -eq 124 ]]; then
                        log "eco: dgpu_disable=1 write TIMED OUT after 10s (driver may have re-bound)"
                    fi
                fi
                actual=$(read_dgpu_disable "$dgpu_path")
                if [[ "$actual" != "1" ]]; then
                    log "eco: first write readback=$actual (expected 1), retrying after PCI rescan"
                    pci_rescan
                    sleep 0.1
                    log "eco: writing dgpu_disable=1 (attempt 2)"
                    if [[ -n "$ROOT" ]]; then
                        echo 1 > "$dgpu_path" 2>/dev/null
                    else
                        timeout 10 bash -c "echo 1 > '$dgpu_path'" 2>/dev/null
                        if [[ $? -eq 124 ]]; then
                            log "eco: dgpu_disable=1 retry TIMED OUT after 10s"
                        fi
                    fi
                    sleep 0.1
                    actual=$(read_dgpu_disable "$dgpu_path")
                fi

                if [[ "$actual" == "1" ]]; then
                    log "eco: dgpu_disable=1 confirmed"
                    reset_eco_retry_counter
                else
                    record_eco_failure "dgpu_disable write readback=$actual after retry"
                    exit 0
                fi
            fi
        fi

        # Persistent Eco: install the block so future boots (especially
        # after a Windows visit) come up driverless. Check the persistent
        # marker FILE (not just IS_PERSISTENT flag) because a one-shot
        # trigger may have fired while the persistent marker also exists.
        if [[ -f "$PERSISTENT_TRIGGER" ]] && [[ "$BACKEND" != "pci" ]]; then
            install_block_artifacts
        fi
        ;;

    standard|optimized)
        # PCI backend: remove blocks, rescan PCI so the dGPU reappears.
        if [[ "$BACKEND" == "pci" ]]; then
            log "$MODE: PCI backend - removing block artifacts, reloading udev, rescanning PCI"
            rm -f "$MODPROBE_BLOCK" "$UDEV_BLOCK" 2>/dev/null
            udevadm_reload
            sleep 0.05
            pci_rescan
            reset_eco_retry_counter
            rm -f "$TRIGGER" 2>/dev/null
            log "$MODE: PCI bus rescan triggered, dGPU should reappear"
            exit 0
        fi

        # Enable dGPU.
        wrote_enable=0
        if [[ -n "$dgpu_path" ]]; then
            current=$(cat "$dgpu_path" 2>/dev/null || echo "0")
            if [[ "$current" == "1" ]]; then
                log "$MODE: writing dgpu_disable=0 (enabling dGPU)"
                echo 0 > "$dgpu_path" 2>/dev/null
                wrote_enable=1
            else
                log "$MODE: dgpu already enabled (dgpu_disable=0)"
            fi
        elif [[ -d "$DEBUGFS_BASE" ]]; then
            DEVID="0x00090020"
            echo "$DEVID" > "$DEBUGFS_BASE/dev_id" 2>/dev/null
            probe=$(cat "$DEBUGFS_BASE/dsts" 2>&1)
            if echo "$probe" | grep -q "No such device"; then
                DEVID="0x00090120"
                log "$MODE: ROG endpoint not supported, trying Vivobook ($DEVID)"
            fi
            log "$MODE: no sysfs, using debugfs raw WMI (DEVS $DEVID, 0)"
            echo "$DEVID" > "$DEBUGFS_BASE/dev_id" 2>/dev/null
            echo 0 > "$DEBUGFS_BASE/ctrl_param" 2>/dev/null
            result=$(cat "$DEBUGFS_BASE/devs" 2>&1)
            log "$MODE: raw WMI result: $result"
            wrote_enable=1
        fi
        # Always rescan PCI to recover from a prior failed Eco that removed
        # the dGPU from the bus.
        sleep 0.05
        pci_rescan
        if (( wrote_enable == 1 )); then
            log "$MODE: PCI bus rescan triggered (write completed)"
        else
            log "$MODE: PCI bus rescan triggered (recovery for prior failed Eco)"
        fi
        reset_eco_retry_counter
        ;;

    ultimate)
        # PCI backend: no MUX hardware, treat as no-op.
        if [[ "$BACKEND" == "pci" ]]; then
            log "ultimate: not applicable in PCI backend (no MUX hardware), clearing trigger"
            rm -f "$TRIGGER" 2>/dev/null
            reset_eco_retry_counter
            exit 0
        fi

        # dGPU must be enabled for Ultimate (MUX=0).
        if [[ -n "$dgpu_path" ]]; then
            current=$(cat "$dgpu_path" 2>/dev/null || echo "0")
            if [[ "$current" == "1" ]]; then
                log "ultimate: writing dgpu_disable=0 (enabling dGPU)"
                echo 0 > "$dgpu_path" 2>/dev/null
            else
                log "ultimate: dgpu already enabled"
            fi
        fi
        sleep 0.05
        pci_rescan
        log "ultimate: PCI bus rescan triggered"
        if [[ -n "$mux_path" ]]; then
            mux_val=$(cat "$mux_path" 2>/dev/null || echo "-1")
            log "ultimate: MUX=$mux_val (expected 0 if latch took effect)"
        fi
        reset_eco_retry_counter
        ;;

    *)
        log "Unknown mode '$MODE' - cleaning up without hardware action"
        ;;
esac

# STEP 5: Clean up.
# Always remove the one-shot trigger.
rm -f "$TRIGGER" 2>/dev/null

# For persistent Eco, keep the modprobe+udev blocks so the dGPU driver
# stays blocked across reboots. Check the persistent marker FILE on disk
# (not just IS_PERSISTENT) because a one-shot trigger may have fired
# while the persistent marker also exists.
if [[ "$MODE" == "eco" ]] && [[ -f "$PERSISTENT_TRIGGER" ]]; then
    log "Persistent Eco: preserving block artifacts (driver stays blocked across reboots)"
else
    rm -f "$MODPROBE_BLOCK" "$UDEV_BLOCK" 2>/dev/null
    udevadm_reload
fi

log "Boot GPU mode application complete"
exit 0
