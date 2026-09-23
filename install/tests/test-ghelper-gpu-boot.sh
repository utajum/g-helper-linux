#!/usr/bin/env bash
# ╔══════════════════════════════════════════════════════════════════════╗
# ║  Test harness for install/ghelper-gpu-boot.sh                        ║
# ║                                                                      ║
# ║  Each scenario builds a sandbox under /tmp/ghelper-test-<name>/      ║
# ║  with fake /sys + /etc trees, runs the boot script with              ║
# ║  GHELPER_TEST_ROOT pointing at the sandbox, and asserts on the       ║
# ║  resulting log output and on-disk file state.                        ║
# ║                                                                      ║
# ║  Run:   bash install/tests/test-ghelper-gpu-boot.sh                  ║
# ║  Single: bash install/tests/test-ghelper-gpu-boot.sh <pattern>       ║
# ╚══════════════════════════════════════════════════════════════════════╝
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BOOT_SCRIPT="$(cd "$SCRIPT_DIR/.." && pwd)/ghelper-gpu-boot.sh"
[[ -x "$BOOT_SCRIPT" ]] || chmod +x "$BOOT_SCRIPT"

if [[ ! -f "$BOOT_SCRIPT" ]]; then
    echo "FATAL: boot script not found at $BOOT_SCRIPT" >&2
    exit 2
fi

PATTERN="${1:-}"

# Test results
TESTS_TOTAL=0
TESTS_PASSED=0
TESTS_FAILED=0
FAILED_NAMES=()

# Per-test sandbox state
SANDBOX=""
LOG_FILE=""

# -- Sandbox helpers ------------------------------------------------------------
new_sandbox() {
    local name="$1"
    SANDBOX="/tmp/ghelper-test-${name//[^A-Za-z0-9_-]/_}"
    rm -rf "$SANDBOX"
    mkdir -p "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi"
    mkdir -p "$SANDBOX/sys/devices/platform/asus-nb-wmi"
    mkdir -p "$SANDBOX/sys/class/firmware-attributes/asus-armoury/attributes"
    mkdir -p "$SANDBOX/sys/bus/pci/devices"
    mkdir -p "$SANDBOX/sys/bus/pci/drivers/amdgpu"
    mkdir -p "$SANDBOX/sys/bus/pci/drivers/nvidia"
    mkdir -p "$SANDBOX/sys/module"
    mkdir -p "$SANDBOX/sys/kernel/debug"
    mkdir -p "$SANDBOX/etc/ghelper"
    mkdir -p "$SANDBOX/etc/modprobe.d"
    mkdir -p "$SANDBOX/etc/udev/rules.d"
    : > "$SANDBOX/sys/bus/pci/rescan"
    LOG_FILE="$SANDBOX/test.log"
}

set_legacy_dgpu()    { echo "$1" > "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable"; }
set_legacy_mux()     { echo "$1" > "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode"; }
set_fwattr_dgpu()    {
    mkdir -p "$SANDBOX/sys/class/firmware-attributes/asus-armoury/attributes/dgpu_disable"
    echo "$1" > "$SANDBOX/sys/class/firmware-attributes/asus-armoury/attributes/dgpu_disable/current_value"
}
set_fwattr_mux()     {
    mkdir -p "$SANDBOX/sys/class/firmware-attributes/asus-armoury/attributes/gpu_mux_mode"
    echo "$1" > "$SANDBOX/sys/class/firmware-attributes/asus-armoury/attributes/gpu_mux_mode/current_value"
}
set_trigger()        { echo "$1" > "$SANDBOX/etc/ghelper/pending-gpu-mode"; }
set_persistent()     { echo "$1" > "$SANDBOX/etc/ghelper/persistent-gpu-mode"; }
set_modprobe_block() { echo "blocked" > "$SANDBOX/etc/modprobe.d/ghelper-gpu-block.conf"; }
set_udev_block()     { echo "rules" > "$SANDBOX/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"; }
set_backend()        { echo "$1" > "$SANDBOX/etc/ghelper/backend"; }
add_module()         { mkdir -p "$SANDBOX/sys/module/$1"; }

# Fake a PCI device. Args: <bdf> <vendor_hex> <boot_vga 0|1> <driver "" or "amdgpu" or "nvidia"> [class]
# class defaults to 0x030000 (VGA controller). Use 0x030200 for 3D controller.
add_pci_device() {
    local bdf="$1" vendor="$2" boot_vga="$3" driver="$4" class="${5:-0x030000}"
    local dev_dir="$SANDBOX/sys/bus/pci/devices/$bdf"
    mkdir -p "$dev_dir"
    echo "$vendor"   > "$dev_dir/vendor"
    echo "$boot_vga" > "$dev_dir/boot_vga"
    echo "$class"    > "$dev_dir/class"
    if [[ -n "$driver" ]]; then
        local drv_dir="$SANDBOX/sys/bus/pci/drivers/$driver"
        mkdir -p "$drv_dir"
        # The script uses readlink -f on driver to extract the basename.
        # Make it point to the absolute path so basename resolves correctly.
        ln -sf "$drv_dir" "$dev_dir/driver"
    fi
}

# Make try_release_nvidia fail (simulates stuck driver that can't be released).
force_release_fail() { : > "$SANDBOX/test-release-fails"; }

# Force the next dgpu_disable readback to return $1 instead of the file content.
# Used to simulate firmware EIO (write returns "success" but the value didn't
# actually get applied). The script's first readback uses `cat`; we intercept
# by leaving the file with the wrong value but having the script's own retry
# fix it on the second pass.
fake_dgpu_readback_initial() {
    # We can't easily intercept cat, so we simulate by populating the
    # dgpu_disable file with a stale value AFTER the script writes to it.
    # Instead we use a simpler approach: prepopulate with the wrong value
    # and rely on the script's own retry loop to write again. To detect that
    # the retry actually ran, we check the log for "(attempt 2)".
    echo "$1" > "$SANDBOX/test-fake-readback"
}

# -- Assertions -----------------------------------------------------------------
expect_log_contains() {
    if ! grep -qE "$1" "$LOG_FILE"; then
        echo "  ASSERT FAIL: log should contain /$1/"
        echo "  --- log ---"
        sed 's/^/    /' "$LOG_FILE"
        echo "  --- end ---"
        return 1
    fi
}

expect_log_NOT_contains() {
    if grep -qE "$1" "$LOG_FILE"; then
        echo "  ASSERT FAIL: log should NOT contain /$1/"
        echo "  --- log ---"
        sed 's/^/    /' "$LOG_FILE"
        echo "  --- end ---"
        return 1
    fi
}

expect_file_content() {
    local file="$SANDBOX$1" expected="$2"
    if [[ ! -f "$file" ]]; then
        echo "  ASSERT FAIL: file $1 missing"
        return 1
    fi
    local actual
    actual=$(cat "$file" | tr -d '[:space:]')
    if [[ "$actual" != "$expected" ]]; then
        echo "  ASSERT FAIL: file $1 contains '$actual', expected '$expected'"
        return 1
    fi
}

expect_file_exists() {
    if [[ ! -f "$SANDBOX$1" ]]; then
        echo "  ASSERT FAIL: file $1 should exist"
        return 1
    fi
}

expect_file_missing() {
    if [[ -f "$SANDBOX$1" ]]; then
        echo "  ASSERT FAIL: file $1 should NOT exist"
        return 1
    fi
}

# -- Test runner ----------------------------------------------------------------
run_boot_script() {
    GHELPER_TEST_ROOT="$SANDBOX" bash "$BOOT_SCRIPT" > "$LOG_FILE" 2>&1
}

scenario() {
    local name="$1"
    if [[ -n "$PATTERN" ]] && [[ "$name" != *"$PATTERN"* ]]; then
        return
    fi
    TESTS_TOTAL=$((TESTS_TOTAL + 1))
    new_sandbox "$name"
    echo "▶ $name"
    if "test_$name"; then
        echo "  PASS"
        TESTS_PASSED=$((TESTS_PASSED + 1))
        rm -rf "$SANDBOX"
    else
        echo "  FAIL ($SANDBOX preserved)"
        TESTS_FAILED=$((TESTS_FAILED + 1))
        FAILED_NAMES+=("$name")
    fi
}

# 
# Scenarios
# 

# 01: No trigger present + MUX OK → script exits cleanly with "nothing to do"
test_01_no_trigger() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    run_boot_script || return 1
    expect_log_contains "No pending mode" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 02: Eco trigger + AMD-iGPU + NVIDIA-dGPU + nvidia bound → defer + install block.
# The refactored script never attempts rmmod. It observes the bound driver,
# installs block artifacts so the driver won't load next boot, and defers.
test_02_eco_amd_igpu_nv_dgpu_nv_loaded() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_module nvidia_modeset
    add_module nvidia_uvm
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu  # iGPU
    add_pci_device 0000:01:00.0 0x10de 0 nvidia  # dGPU
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "try_release_nvidia - simulated success" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 1 || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 03: Eco trigger + AMD-iGPU + NVIDIA-dGPU + nvidia successfully BLOCKED
# (the desired state - modprobe block worked, udev removed dGPU).
# This is the GA402XV case where the OLD script erroneously hit the
# `elif amdgpu loaded → exit` branch. With Bug 1 fix it should proceed.
test_03_eco_amd_igpu_nv_dgpu_nv_blocked() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    # nvidia NOT loaded (modprobe block worked)
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu  # iGPU only
    # dGPU was removed by udev rule → not in PCI tree
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "dGPU is driverless - safe to apply" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 1 || return 1
    expect_log_NOT_contains "amdgpu drives dGPU" || return 1
}

# 04: Eco trigger + Intel-iGPU + NVIDIA-dGPU + nvidia bound, release succeeds.
test_04_eco_intel_igpu_nv_dgpu_nv_loaded() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_module nvidia_modeset
    add_module i915
    add_pci_device 0000:00:02.0 0x8086 1 i915
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "try_release_nvidia - simulated success" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 05: Eco trigger + pure-AMD hybrid where amdgpu drives the dGPU.
# Cannot unload amdgpu safely. Script should defer.
test_05_eco_pure_amd_amdgpu_drives_dgpu() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu  # iGPU
    add_pci_device 0000:01:00.0 0x1002 0 amdgpu  # dGPU also amdgpu
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "amdgpu drives dGPU" || return 1
    expect_log_NOT_contains "dgpu_disable=1 confirmed" || return 1
    # Trigger preserved for next retry
    expect_file_exists /etc/ghelper/pending-gpu-mode || return 1
    expect_file_content /etc/ghelper/eco-retry-count 1 || return 1
}

# 06: Eco trigger + no sysfs + debugfs available → raw WMI path with double-write
test_06_eco_no_sysfs_debugfs() {
    set_legacy_mux 1
    mkdir -p "$SANDBOX/sys/kernel/debug/asus-nb-wmi"
    : > "$SANDBOX/sys/kernel/debug/asus-nb-wmi/dev_id"
    : > "$SANDBOX/sys/kernel/debug/asus-nb-wmi/ctrl_param"
    echo "= 0x1" > "$SANDBOX/sys/kernel/debug/asus-nb-wmi/dsts"
    echo "= 0x1" > "$SANDBOX/sys/kernel/debug/asus-nb-wmi/devs"
    # Remove the dgpu_disable sysfs file (only debugfs available)
    rm -f "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable"
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "no sysfs, using debugfs raw WMI" || return 1
    expect_log_contains "first write" || return 1
    expect_log_contains "second write" || return 1
}

# 07: MUX=0 + eco trigger → discard trigger, force MUX=1 (impossible state)
test_07_mux0_with_eco_trigger() {
    set_legacy_dgpu 0
    set_legacy_mux  0
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "SAFETY: MUX=0 \+ trigger='eco' - discarding" || return 1
    expect_log_contains "forcing MUX=1" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/ghelper/last-recovery || return 1
}

# 08: MUX=0 + dgpu_disable=1 → recovery (force dgpu=0)
test_08_mux0_with_dgpu_disabled() {
    set_legacy_dgpu 1
    set_legacy_mux  0
    run_boot_script || return 1
    expect_log_contains "IMPOSSIBLE STATE, forcing dgpu_disable=0" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 0 || return 1
}

# 09: MUX=0 + modprobe block present → cleanup
test_09_mux0_with_block_artifacts() {
    set_legacy_dgpu 0
    set_legacy_mux  0
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "GPU block artifacts present - removing" || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_missing /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
}

# 10: Standard trigger + dgpu disabled → write 0, PCI rescan
test_10_standard_with_dgpu_disabled() {
    set_legacy_dgpu 1
    set_legacy_mux  1
    set_trigger standard
    run_boot_script || return 1
    expect_log_contains "writing dgpu_disable=0" || return 1
    expect_log_contains "PCI bus rescan triggered" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 0 || return 1
}

# 11: Standard trigger + dgpu already enabled → Bug 5 fix: rescan anyway
test_11_standard_with_dgpu_already_enabled() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger standard
    run_boot_script || return 1
    expect_log_contains "PCI bus rescan triggered \(recovery for prior failed Eco\)" || return 1
    expect_log_contains "dgpu already enabled" || return 1
}

# 12: Optimized trigger same path as Standard
test_12_optimized_with_dgpu_enabled() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger optimized
    run_boot_script || return 1
    expect_log_contains "PCI bus rescan triggered" || return 1
}

# 13: Ultimate trigger with dgpu disabled → enable, rescan
test_13_ultimate_with_dgpu_disabled() {
    set_legacy_dgpu 1
    set_legacy_mux  0
    set_trigger ultimate
    run_boot_script || return 1
    # NB: with MUX=0 + dgpu=1 the safety pass will recover first (Step 1).
    # The trigger is "ultimate" so it survives the safety filter and gets
    # processed in step 4.
    expect_log_contains "PCI bus rescan triggered" || return 1
}

# 14: Empty trigger file → cleanup, no apply
test_14_empty_trigger() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger ""
    run_boot_script || return 1
    expect_log_contains "Empty trigger file" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 15: Unknown trigger value → log and clean up
test_15_unknown_trigger() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger "frobnicate"
    run_boot_script || return 1
    expect_log_contains "Unknown mode 'frobnicate'" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 16: Legacy "1" trigger value (backward compat) → treated as eco
test_16_legacy_1_trigger() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger 1
    run_boot_script || return 1
    expect_log_contains "Pending GPU mode: '1'" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 17: nvidia bound + release fails -> defer with block, counter=1.
test_17_bound_driver_defers_with_block() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "release failed" || return 1
    expect_log_contains "failure #1" || return 1
    expect_file_content /etc/ghelper/eco-retry-count 1 || return 1
    expect_file_exists /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_log_NOT_contains "writing dgpu_disable=1" || return 1
}

# 18: release fails 3 times across runs -> trigger removed, marker written
test_18_rmmod_fails_reaches_giveup() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    echo 2 > "$SANDBOX/etc/ghelper/eco-retry-count"
    run_boot_script || return 1
    expect_log_contains "giving up" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_missing /etc/ghelper/eco-retry-count || return 1
    expect_file_exists /etc/ghelper/last-eco-failed || return 1
}

# 19: Successful Eco apply resets the retry counter
test_19_retry_counter_resets_on_success() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    # Counter had a previous failure
    mkdir -p "$SANDBOX/etc/ghelper"
    echo 2 > "$SANDBOX/etc/ghelper/eco-retry-count"
    echo "prior failure" > "$SANDBOX/etc/ghelper/last-eco-failed"
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_missing /etc/ghelper/eco-retry-count || return 1
    expect_file_missing /etc/ghelper/last-eco-failed || return 1
}

# 20: Firmware-attributes (asus-armoury) path only (no legacy node)
test_20_eco_fwattrs_only() {
    rm -rf "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi"
    rm -rf "$SANDBOX/sys/devices/platform/asus-nb-wmi"
    set_fwattr_dgpu 0
    set_fwattr_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dgpu_disable resolved.*firmware-attributes" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_content /sys/class/firmware-attributes/asus-armoury/attributes/dgpu_disable/current_value 1 || return 1
}

# 21: Eco trigger but dgpu_disable already 1 → no-op success, counter resets
test_21_eco_already_disabled() {
    set_legacy_dgpu 1
    set_legacy_mux  1
    set_trigger eco
    mkdir -p "$SANDBOX/etc/ghelper"
    echo 1 > "$SANDBOX/etc/ghelper/eco-retry-count"
    run_boot_script || return 1
    expect_log_contains "dgpu_disable already 1" || return 1
    expect_file_missing /etc/ghelper/eco-retry-count || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 22: Eco + MUX=0 trigger arrives mid-flight (e.g. user changed mind, MUX latched)
# Safety pass catches this and discards the impossible trigger before step 4.
test_22_eco_trigger_with_mux0_user_latched() {
    set_legacy_dgpu 0
    set_legacy_mux  0
    set_trigger eco
    set_modprobe_block
    run_boot_script || return 1
    expect_log_contains "discarding impossible Eco trigger" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    # Safety pass also forces MUX=1 and reloads udev
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
}

# 23: amdgpu module loaded but no PCI device bound to it → safe to proceed
# (Edge case: amdgpu module was inserted but never bound, e.g. amd-pstate driver
# module loaded for power management on Intel system with stale amdgpu loaded.)
test_23_amdgpu_module_loaded_but_unbound() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    # NO PCI devices bound to amdgpu
    add_pci_device 0000:01:00.0 0x10de 0 nvidia  # NVIDIA dGPU was removed by udev → no driver
    rm -f "$SANDBOX/sys/bus/pci/devices/0000:01:00.0/driver"
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU is driverless - safe to apply" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 24: udevadm settle is invoked before apply step (Bug 3 fix)
test_24_udevadm_settle_invoked() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "would udevadm settle" || return 1
}

# 25: Bug 2 fix - first write readback fails, retry with PCI rescan succeeds.
# Simulates GA402XV.318 firmware behavior: first dgpu_disable=1 store appears
# successful at the kernel level but doesn't take effect; PCI rescan then
# second store applies it.
test_25_eco_double_write_retry() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    # Tell test wrapper to lie on the next 2 dgpu_disable readbacks:
    #   - 1st call: initial state check (sees "0", same as actual file)
    #   - 2nd call: post-first-write readback (lie returns "0" → retry path)
    # Subsequent reads return the truth (file = "1" after the second write).
    echo 2 > "$SANDBOX/test-dgpu-readback-fail-count"
    run_boot_script || return 1
    expect_log_contains "writing dgpu_disable=1 \(attempt 1\)" || return 1
    expect_log_contains "first write readback=0.*retrying after PCI rescan" || return 1
    expect_log_contains "writing dgpu_disable=1 \(attempt 2\)" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 1 || return 1
}

# -- New coverage (#27–#38) ----------------------------------------------------

# 27: nouveau (open-source NVIDIA driver) bound to dGPU. Script detects the bound
# driver, installs block artifacts, and defers cleanly (no rmmod attempt).
test_27_eco_nouveau_loaded() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nouveau
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu     # iGPU
    add_pci_device 0000:01:00.0 0x10de 0 nouveau    # dGPU on open driver
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "try_release_nvidia - simulated success" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 28: Device with no gpu_mux_mode hardware exposed (cheaper TUF chassis).
# Step 1 should skip the MUX safety check entirely; step 4 should still apply.
test_28_no_mux_hardware() {
    set_legacy_dgpu 0
    # No mux file at all
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "gpu_mux_mode: not found" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_log_NOT_contains "SAFETY: MUX=0" || return 1
}

# 29: vfio-pci bound to dGPU (passthrough). Script should defer via record_eco_failure.
test_29_vfio_pci_passthrough() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu     # iGPU
    # NVIDIA dGPU bound to vfio-pci (user is running a VM with GPU passthrough)
    add_pci_device 0000:01:00.0 0x10de 0 vfio-pci
    # Add the class file so dgpu_foreign_driver recognises it as VGA
    echo "0x030000" > "$SANDBOX/sys/bus/pci/devices/0000:01:00.0/class"
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "foreign driver 'vfio-pci'" || return 1
    expect_log_contains "refusing to disable" || return 1
    expect_file_exists /etc/ghelper/pending-gpu-mode || return 1
    expect_file_content /etc/ghelper/eco-retry-count 1 || return 1
    expect_log_NOT_contains "dgpu_disable=1 confirmed" || return 1
}

# 30: Multiple NVIDIA dGPUs (rare - mobile workstation 4090+4060).
# Both are bound → script detects the bound driver and defers.
test_30_multiple_nv_dgpus() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    add_pci_device 0000:02:00.0 0x10de 0 nvidia
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "try_release_nvidia - simulated success" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 31: dGPU has driver bound but boot_vga file is missing (broken / old kernel).
# `cat boot_vga 2>/dev/null || echo "0"` should default to "0" → treated as dGPU.
test_31_dgpu_missing_boot_vga() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu     # iGPU
    add_pci_device 0000:01:00.0 0x1002 0 amdgpu     # AMD dGPU
    # Remove boot_vga file to simulate broken kernel
    rm -f "$SANDBOX/sys/bus/pci/devices/0000:01:00.0/boot_vga"
    set_trigger eco
    run_boot_script || return 1
    # Default to "0" → amdgpu_drives_dgpu returns true → defer
    expect_log_contains "amdgpu drives dGPU" || return 1
}

# 32: dGPU has driver bound but vendor file is missing. Probe skips silently.
test_32_dgpu_missing_vendor() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    add_pci_device 0000:01:00.0 0x1002 0 amdgpu
    rm -f "$SANDBOX/sys/bus/pci/devices/0000:01:00.0/vendor"
    set_trigger eco
    run_boot_script || return 1
    # Vendor empty → loop skips device → driverless path
    expect_log_contains "dGPU is driverless - safe to apply" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 33: Trigger file contains surrounding whitespace and newline.
test_33_trigger_with_whitespace() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    # Write trigger via printf to embed leading/trailing whitespace + newline
    printf '  eco  \n' > "$SANDBOX/etc/ghelper/pending-gpu-mode"
    run_boot_script || return 1
    expect_log_contains "Pending GPU mode: 'eco'" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 34: Trigger file is only whitespace / a bare newline → treated as empty.
test_34_trigger_newline_only() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    printf '\n' > "$SANDBOX/etc/ghelper/pending-gpu-mode"
    run_boot_script || return 1
    expect_log_contains "Empty trigger file" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 35: Trigger file has Windows-style CRLF line endings.
test_35_trigger_crlf() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    printf 'eco\r\n' > "$SANDBOX/etc/ghelper/pending-gpu-mode"
    run_boot_script || return 1
    expect_log_contains "Pending GPU mode: 'eco'" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 36: Uppercase trigger value falls through to "unknown mode" branch
# (case statement is case-sensitive, per your directive to keep as-is).
test_36_uppercase_trigger() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger "ECO"
    run_boot_script || return 1
    expect_log_contains "Unknown mode 'ECO'" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 37: SKIPPED per your directive (symlink trigger - won't occur in practice).

# 38: Retry counter file contains non-numeric garbage (corruption recovery).
test_38_counter_file_corrupted() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    mkdir -p "$SANDBOX/etc/ghelper"
    echo "not a number" > "$SANDBOX/etc/ghelper/eco-retry-count"
    run_boot_script || return 1
    # Sanitization should treat the garbage as 0, increment to 1
    expect_file_content /etc/ghelper/eco-retry-count 1 || return 1
}

# 39: Retry counter with negative number (defensive sanitization).
test_39_counter_negative() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    mkdir -p "$SANDBOX/etc/ghelper"
    echo "-5" > "$SANDBOX/etc/ghelper/eco-retry-count"
    run_boot_script || return 1
    expect_file_content /etc/ghelper/eco-retry-count 1 || return 1
}

# 40: Retry counter with very large valid number -> gives up.
test_40_counter_large_number() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    mkdir -p "$SANDBOX/etc/ghelper"
    echo "9000000000" > "$SANDBOX/etc/ghelper/eco-retry-count"
    run_boot_script || return 1
    expect_log_contains "giving up" || return 1
    expect_file_exists /etc/ghelper/last-eco-failed || return 1
}

# 41: dgpu_disable sysfs file contains unexpected garbage (e.g. "abc").
# Script reads "abc", treats != "1" → tries write; the write will store "1";
# readback returns "1"; success.
test_41_dgpu_disable_garbage() {
    set_legacy_dgpu "abc"
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 1 || return 1
}

# 42: gpu_mux_mode file contains unexpected garbage. Step 1 mux comparison
# fails ("abc" != "0") → safety branch skipped → step 4 proceeds.
test_42_mux_mode_garbage() {
    set_legacy_dgpu 0
    set_legacy_mux  "garbage"
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    run_boot_script || return 1
    expect_log_NOT_contains "SAFETY: MUX=0" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 43: /etc/ghelper directory missing entirely (fresh first install).
# record_eco_failure and the recovery branch both `mkdir -p`. Trigger an
# Eco failure that needs to write the counter.
test_43_etc_ghelper_missing() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    # Trigger written BEFORE we nuke the dir
    set_trigger eco
    # Now remove the directory but keep the trigger location reachable via /etc
    # The script's mkdir -p must re-create it before writing the counter.
    # (We can't delete the parent dir without losing the trigger file too,
    # so simulate by deleting just the artifacts the script needs to write.)
    rm -rf "$SANDBOX/etc/ghelper"
    # Re-create just enough to hold the trigger again
    mkdir -p "$SANDBOX/etc/ghelper"
    set_trigger eco
    rm -rf "$SANDBOX/etc/ghelper"  # final teardown - trigger gone too
    # Without a trigger file we don't reach the apply path. Re-create one
    # AND remove the eco-retry-count so we can verify mkdir works.
    # Workaround: set the trigger via a path that the script can read after
    # mkdir -p. The trigger path is under /etc/ghelper so we have to test the
    # mkdir success without first deleting the trigger. Instead, delete all
    # CHILDREN of /etc/ghelper but keep the dir + trigger.
    mkdir -p "$SANDBOX/etc/ghelper"
    force_release_fail
    set_trigger eco
    run_boot_script || return 1
    # The retry counter should now exist, proving mkdir + write worked.
    expect_file_exists /etc/ghelper/eco-retry-count || return 1
}

# 44: /sys/bus/pci/rescan file missing (minimal kernel, chroot).
# pci_rescan should silently skip without erroring out the script.
test_44_pci_rescan_missing() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    rm -f "$SANDBOX/sys/bus/pci/rescan"
    set_trigger eco
    # Force the double-write retry path to also test pci_rescan no-file path
    echo 2 > "$SANDBOX/test-dgpu-readback-fail-count"
    run_boot_script || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 45: logger command unavailable (minimal busybox environment).
# log() falls back to echo via `|| true`. Verify stdout still shows lines.
test_45_logger_missing() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger eco
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    # Override PATH so logger isn't reachable
    GHELPER_TEST_ROOT="$SANDBOX" PATH="/tmp/nopath:/bin" bash "$BOOT_SCRIPT" > "$LOG_FILE" 2>&1 || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 46: /sys/bus/pci/devices directory empty (LiveCD / chroot with no devices).
# amdgpu_drives_dgpu walks the empty dir and returns 1 → safe to proceed.
test_46_pci_devices_empty() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    # NO PCI devices at all
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU is driverless - safe to apply" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 47: /sys/bus/pci/devices directory missing entirely.
test_47_pci_devices_dir_missing() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    rm -rf "$SANDBOX/sys/bus/pci/devices"
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 48: Step 1 safety recovery happens FIRST, then step 4 still processes the
# trigger. Scenario: trigger=standard + MUX=0 + dgpu=1 (impossible state).
# Step 1 forces dgpu=0 + MUX=1, doesn't discard non-eco trigger, then step 4
# sees trigger=standard with dgpu=0 → rescan branch.
test_48_safety_then_standard_apply() {
    set_legacy_dgpu 1
    set_legacy_mux  0
    set_trigger standard
    run_boot_script || return 1
    expect_log_contains "forcing dgpu_disable=0" || return 1
    expect_log_contains "forcing MUX=1" || return 1
    expect_log_contains "PCI bus rescan triggered" || return 1
    # The recovery marker SHOULD be written; trigger SHOULD be removed by step 5.
    expect_file_exists /etc/ghelper/last-recovery || return 1
}

# 49: Eco trigger + already in Eco + previous failure counter present.
# Should reset counter + marker without writing anything new.
test_49_already_eco_with_prior_failures() {
    set_legacy_dgpu 1
    set_legacy_mux  1
    set_trigger eco
    mkdir -p "$SANDBOX/etc/ghelper"
    echo 2 > "$SANDBOX/etc/ghelper/eco-retry-count"
    echo "prior failure" > "$SANDBOX/etc/ghelper/last-eco-failed"
    run_boot_script || return 1
    expect_log_contains "dgpu_disable already 1" || return 1
    expect_file_missing /etc/ghelper/eco-retry-count || return 1
    expect_file_missing /etc/ghelper/last-eco-failed || return 1
}

# 50: Standard trigger via debugfs-only path (no sysfs dgpu_disable file).
test_50_standard_via_debugfs() {
    set_legacy_mux 1
    rm -f "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable"
    mkdir -p "$SANDBOX/sys/kernel/debug/asus-nb-wmi"
    : > "$SANDBOX/sys/kernel/debug/asus-nb-wmi/dev_id"
    : > "$SANDBOX/sys/kernel/debug/asus-nb-wmi/ctrl_param"
    echo "= 0x0" > "$SANDBOX/sys/kernel/debug/asus-nb-wmi/dsts"
    echo "= 0x0" > "$SANDBOX/sys/kernel/debug/asus-nb-wmi/devs"
    set_trigger standard
    run_boot_script || return 1
    expect_log_contains "no sysfs, using debugfs raw WMI \(DEVS 0x00090020, 0\)" || return 1
    expect_log_contains "PCI bus rescan triggered" || return 1
}

# 51: Ultimate trigger on hardware without MUX (mux_path empty).
# Step 4 writes dgpu_disable=0 + rescans + skips the MUX log line.
test_51_ultimate_no_mux() {
    set_legacy_dgpu 1
    # No mux file
    set_trigger ultimate
    run_boot_script || return 1
    expect_log_contains "writing dgpu_disable=0" || return 1
    expect_log_contains "PCI bus rescan triggered" || return 1
    expect_log_NOT_contains "ultimate: MUX=" || return 1
}

# 52/53: After successful Eco apply, ALL THREE artifacts are removed AND
# udevadm_reload was called.
test_52_full_cleanup_after_eco_success() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_missing /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
    expect_log_contains "would udevadm control --reload-rules" || return 1
    expect_log_contains "Boot GPU mode application complete" || return 1
}

# 54: nvidia bound → defer + install block artifacts. Existing blocks are
# overwritten by install_block_artifacts. Trigger preserved for next boot.
test_54_failed_eco_preserves_blocks() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "release failed" || return 1
    expect_file_exists /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_exists /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
}

#
# PCI backend scenarios (boot script never writes dgpu_disable;
# block artifacts ARE the persistent Eco state).
#

# 55: PCI backend + nvidia bound → defer + install block. The dgpu_driver_bound
# check fires before the PCI backend code path, so PCI-specific logs do not appear.
test_55_pci_eco_nv_loaded() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_module nvidia_modeset
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    # Release succeeds, then PCI backend early exit
    expect_log_contains "try_release_nvidia - simulated success" || return 1
    expect_log_contains "PCI backend - blocks remain as persistent state" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 56: PCI eco trigger but driver already unloaded → driverless → PCI backend
# early exit, blocks preserved as persistent state.
test_56_pci_eco_already_unloaded() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "dGPU is driverless - safe to apply" || return 1
    expect_log_contains "PCI backend - blocks remain as persistent state" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_exists /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
}

# 57: PCI backend + nvidia bound + release fails -> defer with block.
test_57_pci_eco_nv_bound() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "release failed" || return 1
    expect_file_exists /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 58: PCI eco + release fails 3rd attempt -> give-up marker, blocks preserved
test_58_pci_eco_rmmod_fail_giveup() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    set_modprobe_block
    set_udev_block
    echo "2" > "$SANDBOX/etc/ghelper/eco-retry-count"
    run_boot_script || return 1
    expect_log_contains "reached 3 failed attempts" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/ghelper/last-eco-failed || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_exists /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
}

# 59: PCI standard with blocks present → blocks removed, PCI rescan, trigger cleared
test_59_pci_standard_clears_blocks() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger standard
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "PCI backend - removing block artifacts" || return 1
    expect_log_contains "PCI bus rescan triggered" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_missing /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
}

# 60: PCI standard, no blocks present (idempotent) - PCI rescan still happens
test_60_pci_standard_no_blocks() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger standard
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "PCI backend - removing block artifacts" || return 1
    expect_log_contains "PCI bus rescan triggered" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 61: PCI optimized trigger → no-op, blocks remain in place if present
test_61_pci_optimized_noop() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger optimized
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    # optimized is handled in the standard|optimized branch with the same
    # cleanup, so it removes blocks too.
    expect_log_contains "removing block artifacts" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
}

# 62: PCI ultimate trigger → log "not applicable", trigger cleared, no rescan
test_62_pci_ultimate_noop() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger ultimate
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "ultimate: not applicable in PCI backend" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    # blocks left alone (no transition occurred)
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 63: PCI backend on system with NO asus-nb-wmi / asus-armoury + nvidia bound → defer.
# The dgpu_driver_bound check fires before PCI backend code, so we get the
# standard bound-driver defer regardless of missing ASUS sysfs.
test_63_pci_no_asus_hardware() {
    set_backend pci
    rm -rf "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi"
    rm -rf "$SANDBOX/sys/devices/platform/asus-nb-wmi"
    rm -rf "$SANDBOX/sys/class/firmware-attributes"
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    # Release succeeds, then PCI backend early exit (no dgpu_disable sysfs)
    expect_log_contains "try_release_nvidia - simulated success" || return 1
    expect_log_contains "PCI backend - blocks remain" || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 64: PCI backend RUNS the MUX safety check too (universal recovery).
# Previously this test pinned the asus-wmi-only gate; that gate was a
# security gap because MUX=0 + udev hot-remove yields the same black
# screen as MUX=0 + dgpu_disable=1. Now both backends recover.
test_64_pci_runs_mux_safety() {
    set_backend pci
    set_legacy_dgpu 1      # MUX=0 + dgpu_disable=1 must be recovered
    set_legacy_mux  0
    set_modprobe_block     # MUX=0 + blocks must be recovered
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "IMPOSSIBLE STATE, forcing dgpu_disable=0" || return 1
    expect_log_contains "GPU block artifacts present - removing" || return 1
    expect_log_contains "backend=pci recovery" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 0 || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 65: PCI backend + foreign driver (vfio-pci) bound → defer
test_65_pci_with_vfio_passthrough() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_pci_device 0000:01:00.0 0x10de 0 vfio-pci
    mkdir -p "$SANDBOX/sys/bus/pci/drivers/vfio-pci"
    # class 0x030000 (VGA) so dgpu_foreign_driver detects it
    echo "0x030000" > "$SANDBOX/sys/bus/pci/devices/0000:01:00.0/class"
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "foreign driver" || return 1
    expect_file_exists /etc/ghelper/pending-gpu-mode || return 1
}

# 66: PCI backend + nouveau bound → defer + install block
test_66_pci_nouveau_loaded() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nouveau
    add_pci_device 0000:01:00.0 0x10de 0 nouveau
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "try_release_nvidia - simulated success" || return 1
    expect_log_contains "PCI backend - blocks remain" || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 67: Garbage backend file → fall back to asus-wmi default
test_67_backend_garbage() {
    set_backend "garbage-value"
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: asus-wmi" || return 1
    expect_log_contains "unknown value" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
}

# 68: backend file with leading/trailing whitespace and newlines
test_68_backend_with_whitespace() {
    printf "  pci  \n" > "$SANDBOX/etc/ghelper/backend"
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_trigger eco
    set_modprobe_block
    set_udev_block
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 69: backend file missing → asus-wmi default (regression check vs. old script)
test_69_backend_missing() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: asus-wmi" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 70: explicit asus-wmi backend behaves identically to legacy (regression)
test_70_explicit_asus_wmi() {
    set_backend "asus-wmi"
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: asus-wmi" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

#
# PCI backend + MUX=0 impossible-state recovery (universal boot safety).
# The boot script must recover the same way as asus-wmi when MUX=0 sits on
# top of eco state - without this, a user who clicked Ultimate then enabled
# PCI mode then clicked Eco gets a black screen on next boot.
#

# 71: pci + MUX=0 + trigger=eco + blocks → discard trigger, remove blocks, force MUX=1
test_71_pci_mux0_with_eco_trigger() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  0
    set_trigger eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "discarding impossible Eco trigger" || return 1
    expect_log_contains "GPU block artifacts present - removing" || return 1
    expect_log_contains "backend=pci recovery" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_missing /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
    expect_file_exists /etc/ghelper/last-recovery || return 1
}

# 72: pci + MUX=0 + blocks present, no trigger → remove blocks, force MUX=1
test_72_pci_mux0_with_blocks_no_trigger() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  0
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "GPU block artifacts present - removing" || return 1
    expect_log_contains "backend=pci recovery" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_missing /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
    expect_file_exists /etc/ghelper/last-recovery || return 1
}

# 73: pci + MUX=0 + clean state → no recovery needed, no marker written
test_73_pci_mux0_clean_state() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  0
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "MUX=0 safety check complete" || return 1
    expect_log_NOT_contains "backend=pci recovery" || return 1
    expect_file_missing /etc/ghelper/last-recovery || return 1
}

# 74: pci + MUX=0 + trigger=standard + blocks → trigger preserved (safe),
#     blocks removed, MUX latched to 1. Boot script then proceeds to STEP 4
#     standard branch which removes blocks + rescans PCI.
test_74_pci_mux0_with_standard_trigger() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  0
    set_trigger standard
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_NOT_contains "discarding impossible Eco trigger" || return 1
    expect_log_contains "GPU block artifacts present - removing" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
    # blocks must end up gone (either by SAFETY recovery or STEP 4 cleanup)
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_missing /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
}

# 75: pci + MUX=0 + legacy '1' trigger (alias for eco) + blocks → same recovery as eco
test_75_pci_mux0_with_legacy_1_trigger() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  0
    set_trigger 1
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "backend: pci" || return 1
    expect_log_contains "discarding impossible Eco trigger" || return 1
    expect_log_contains "backend=pci recovery" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 26: Bug 2 - first write fails AND PCI rescan doesn't help (persistent EIO)
# Should record a failure via the retry counter and exit cleanly.
test_26_eco_double_write_both_fail() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_trigger eco
    # The wrapper only returns bad once, but we override actual sysfs to "0"
    # after the test wrapper consumes its flag, forcing both reads to fail.
    # Implementation: pre-create dgpu_disable as a directory so `echo > path`
    # fails silently (>/dev/null), leaving the dummy file empty → reads "0".
    rm -f "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable"
    mkdir -p "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable.d"
    # Use a regular file but make it read-only AND owned by root would require
    # privileges. Simplest: leave it empty + chmod 0444 so write fails.
    : > "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable"
    chmod 0444 "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable"
    run_boot_script || return 1
    expect_log_contains "writing dgpu_disable=1 \(attempt 2\)" || return 1
    expect_log_contains "failure #1" || return 1
    expect_file_exists /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/ghelper/eco-retry-count || return 1
    # cleanup chmod so harness can rm -rf the sandbox
    chmod 0644 "$SANDBOX/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable" 2>/dev/null || true
}

#
# Persistent Eco scenarios (persistent-gpu-mode file survives reboots)
#

# 76: Persistent eco, no one-shot trigger → eco applied via persistent file
test_76_persistent_eco_no_trigger() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_persistent eco
    run_boot_script || return 1
    expect_log_contains "Persistent GPU mode: 'eco'" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    # One-shot trigger must NOT exist (never was created)
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    # Persistent file MUST survive cleanup
    expect_file_exists /etc/ghelper/persistent-gpu-mode || return 1
    expect_file_content /etc/ghelper/persistent-gpu-mode eco || return 1
}

# 77: Both one-shot and persistent present → one-shot takes priority
test_77_oneshot_overrides_persistent() {
    set_legacy_dgpu 1
    set_legacy_mux  1
    set_trigger standard
    set_persistent eco
    run_boot_script || return 1
    expect_log_contains "Pending GPU mode: 'standard'" || return 1
    expect_log_NOT_contains "Persistent GPU mode" || return 1
    # One-shot trigger consumed
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    # Persistent file survives (not touched during standard apply)
    expect_file_exists /etc/ghelper/persistent-gpu-mode || return 1
}

# 78: Persistent eco + MUX=0 → safety check removes persistent marker
test_78_persistent_eco_mux0_safety() {
    set_legacy_dgpu 0
    set_legacy_mux  0
    set_persistent eco
    run_boot_script || return 1
    expect_log_contains "SAFETY: MUX=0 \\+ persistent='eco' - discarding impossible persistent Eco" || return 1
    expect_log_contains "MUX=0 safety check complete" || return 1
    # Persistent marker must be gone (impossible state)
    expect_file_missing /etc/ghelper/persistent-gpu-mode || return 1
}

# 79: Persistent eco preserves block artifacts across STEP 5 cleanup (Bug 6
# part 3 / Part B). The block keeps the dGPU driver from binding after a
# Windows visit, so future boots come up driverless and never deadlock.
test_79_persistent_preserves_blocks() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module amdgpu
    add_pci_device 0000:65:00.0 0x1002 1 amdgpu
    set_persistent eco
    run_boot_script || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_log_contains "Persistent Eco: preserving block artifacts" || return 1
    expect_log_contains "Boot GPU mode application complete" || return 1
    # One-shot trigger gone, persistent survives
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_exists /etc/ghelper/persistent-gpu-mode || return 1
    # Block artifacts PRESERVED (installed by persistent eco, kept by STEP 5)
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_exists /etc/udev/rules.d/50-ghelper-remove-dgpu.rules || return 1
}

# 80: Persistent eco + already in eco (dgpu=1) → no-op but persistent survives
test_80_persistent_eco_already_applied() {
    set_legacy_dgpu 1
    set_legacy_mux  1
    set_persistent eco
    run_boot_script || return 1
    expect_log_contains "Persistent GPU mode: 'eco'" || return 1
    expect_log_contains "dgpu_disable already 1" || return 1
    expect_file_exists /etc/ghelper/persistent-gpu-mode || return 1
}

# 81: Persistent eco + PCI backend → blocks preserved, persistent survives
test_81_persistent_eco_pci_backend() {
    set_backend pci
    set_legacy_dgpu 0
    set_legacy_mux  1
    set_persistent eco
    set_modprobe_block
    set_udev_block
    run_boot_script || return 1
    expect_log_contains "Persistent GPU mode: 'eco'" || return 1
    expect_log_contains "PCI backend - blocks remain as persistent state" || return 1
    expect_file_exists /etc/ghelper/persistent-gpu-mode || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
}

# 82: No trigger AND no persistent → nothing to do (baseline unchanged)
test_82_no_trigger_no_persistent() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    run_boot_script || return 1
    expect_log_contains "No pending mode - nothing to do" || return 1
}

# 83: NVIDIA module loaded (/sys/module/nvidia exists) but dGPU PCI device has
# NO driver symlink (previous boot's block prevented binding). → driverless →
# dgpu_disable=1 succeeds. This is the normal "second boot after block" flow.
test_83_eco_nvidia_module_not_bound() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia         # nvidia module still in /sys/module
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 ""   # NVIDIA dGPU, NO driver bound
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU is driverless - safe to apply" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    # Must NOT have detected a bound driver
    expect_log_NOT_contains "dGPU driver is bound" || return 1
}

# 84: nvidia bound + release fails -> defer with block, never write dgpu_disable.
test_84_eco_bound_driver_defers_no_hang() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "release failed" || return 1
    expect_log_NOT_contains "writing dgpu_disable=1" || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_exists /etc/ghelper/pending-gpu-mode || return 1
}

# 85: Persistent eco + nvidia bound + release fails -> defer with block.
# Persistent marker survives the defer.
test_85_persistent_eco_nvidia_bound_defers() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_module nvidia_modeset
    add_module nvidia_uvm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_persistent eco
    run_boot_script || return 1
    expect_log_contains "Persistent GPU mode: 'eco'" || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "release failed" || return 1
    expect_file_exists /etc/ghelper/persistent-gpu-mode || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_log_NOT_contains "dgpu_disable=1 confirmed" || return 1
}

# 86: Driverless NVIDIA dGPU (PCI device exists but no driver symlink, no nvidia
# module loaded). The script proceeds directly to dgpu_disable=1.
test_86_eco_driverless_nvidia_dgpu() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    # nvidia NOT loaded - no module dirs
    add_pci_device 0000:01:00.0 0x10de 0 ""
    set_trigger eco
    run_boot_script || return 1
    expect_log_contains "dGPU is driverless - safe to apply" || return 1
    expect_log_contains "dgpu_disable=1 confirmed" || return 1
    # Must NOT have detected a bound driver
    expect_log_NOT_contains "dGPU driver is bound" || return 1
}

# 87: Persistent eco + nvidia bound + counter already at 2 → third failure
# crosses MAX_ECO_RETRIES → gives up, writes failure marker. But persistent
# marker survives (record_eco_failure only removes the one-shot trigger).
test_87_persistent_eco_bound_driver_defers() {
    set_legacy_dgpu 0
    set_legacy_mux  1
    add_module nvidia
    add_module nvidia_drm
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    force_release_fail
    set_persistent eco
    echo 2 > "$SANDBOX/etc/ghelper/eco-retry-count"
    run_boot_script || return 1
    expect_log_contains "dGPU driver is bound" || return 1
    expect_log_contains "reached 3 failed attempts - giving up" || return 1
    expect_log_NOT_contains "writing dgpu_disable=1" || return 1
    # Block installed for next boot
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    # Persistent marker survives give-up (only one-shot trigger is removed)
    expect_file_exists /etc/ghelper/persistent-gpu-mode || return 1
    # Failure marker written, counter cleaned up
    expect_file_exists /etc/ghelper/last-eco-failed || return 1
    expect_file_missing /etc/ghelper/eco-retry-count || return 1
}

# 88: MUX=0 + persistent eco + nvidia bound (dual-boot from Windows Ultimate).
# STEP 1 safety: MUX=0 with persistent eco marker and blocks on disk.
# Recovery must discard persistent marker, remove blocks, force MUX=1.
test_88_mux0_persistent_eco_nvidia_bound() {
    set_legacy_dgpu 0
    set_legacy_mux  0
    add_module nvidia
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    set_persistent eco
    set_modprobe_block
    run_boot_script || return 1
    expect_log_contains "SAFETY: MUX=0 \\+ persistent='eco' - discarding impossible persistent Eco" || return 1
    expect_log_contains "SAFETY: MUX=0 \\+ GPU block artifacts present" || return 1
    expect_log_contains "recovery - forcing MUX=1" || return 1
    expect_file_missing /etc/ghelper/persistent-gpu-mode || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
    expect_file_exists /etc/ghelper/last-recovery || return 1
}

# 89: MUX=0 + one-shot eco trigger + blocks (deferred after release failure,
# then user switched to Ultimate in Windows). Recovery must discard trigger,
# remove blocks, force MUX=1.
test_89_mux0_oneshot_eco_with_blocks() {
    set_legacy_dgpu 0
    set_legacy_mux  0
    add_module nvidia
    add_pci_device 0000:01:00.0 0x10de 0 nvidia
    set_trigger eco
    set_modprobe_block
    run_boot_script || return 1
    expect_log_contains "SAFETY: MUX=0 \\+ trigger='eco' - discarding impossible Eco trigger" || return 1
    expect_log_contains "SAFETY: MUX=0 \\+ GPU block artifacts present" || return 1
    expect_log_contains "recovery - forcing MUX=1" || return 1
    expect_file_missing /etc/ghelper/pending-gpu-mode || return 1
    expect_file_missing /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 1 || return 1
}

# 90: MUX=0 + no eco artifacts (clean Ultimate boot). STEP 1 passes with no
# recovery needed. Service exits early (no trigger).
test_90_mux0_clean_ultimate() {
    set_legacy_dgpu 0
    set_legacy_mux  0
    add_module nvidia
    add_pci_device 0000:01:00.0 0x10de 1 nvidia
    run_boot_script || return 1
    expect_log_contains "MUX=0 safety check complete" || return 1
    expect_log_NOT_contains "recovery" || return 1
    expect_log_NOT_contains "IMPOSSIBLE" || return 1
    expect_file_content /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode 0 || return 1
}

# Scenario 91: pre-v1.0.94 block file with an amdgpu line (#180).
# PCI backend keeps the file across boots, so it must be repaired in place
# and the nvidia lines must survive.
test_91_stale_amdgpu_block_stripped() {
    set_backend pci
    printf 'install nvidia /bin/false\ninstall amdgpu /bin/false\ninstall nouveau /bin/false\n' \
        > "$SANDBOX/etc/modprobe.d/ghelper-gpu-block.conf"
    run_boot_script || return 1
    expect_log_contains "removed stale amdgpu block" || return 1
    expect_file_exists /etc/modprobe.d/ghelper-gpu-block.conf || return 1
    if grep -q amdgpu "$SANDBOX/etc/modprobe.d/ghelper-gpu-block.conf"; then
        echo "  ASSERT FAIL: amdgpu line still present"
        return 1
    fi
    if ! grep -q '^install nvidia ' "$SANDBOX/etc/modprobe.d/ghelper-gpu-block.conf"; then
        echo "  ASSERT FAIL: nvidia line was lost"
        return 1
    fi
}

# -- Run ------------------------------------------------------------------------
echo "═"
echo " ghelper-gpu-boot.sh scenario tests"
echo "═"

for name in \
    01_no_trigger \
    02_eco_amd_igpu_nv_dgpu_nv_loaded \
    03_eco_amd_igpu_nv_dgpu_nv_blocked \
    04_eco_intel_igpu_nv_dgpu_nv_loaded \
    05_eco_pure_amd_amdgpu_drives_dgpu \
    06_eco_no_sysfs_debugfs \
    07_mux0_with_eco_trigger \
    08_mux0_with_dgpu_disabled \
    09_mux0_with_block_artifacts \
    10_standard_with_dgpu_disabled \
    11_standard_with_dgpu_already_enabled \
    12_optimized_with_dgpu_enabled \
    13_ultimate_with_dgpu_disabled \
    14_empty_trigger \
    15_unknown_trigger \
    16_legacy_1_trigger \
    17_bound_driver_defers_with_block \
    18_rmmod_fails_reaches_giveup \
    19_retry_counter_resets_on_success \
    20_eco_fwattrs_only \
    21_eco_already_disabled \
    22_eco_trigger_with_mux0_user_latched \
    23_amdgpu_module_loaded_but_unbound \
    24_udevadm_settle_invoked \
    25_eco_double_write_retry \
    26_eco_double_write_both_fail \
    27_eco_nouveau_loaded \
    28_no_mux_hardware \
    29_vfio_pci_passthrough \
    30_multiple_nv_dgpus \
    31_dgpu_missing_boot_vga \
    32_dgpu_missing_vendor \
    33_trigger_with_whitespace \
    34_trigger_newline_only \
    35_trigger_crlf \
    36_uppercase_trigger \
    38_counter_file_corrupted \
    39_counter_negative \
    40_counter_large_number \
    41_dgpu_disable_garbage \
    42_mux_mode_garbage \
    43_etc_ghelper_missing \
    44_pci_rescan_missing \
    45_logger_missing \
    46_pci_devices_empty \
    47_pci_devices_dir_missing \
    48_safety_then_standard_apply \
    49_already_eco_with_prior_failures \
    50_standard_via_debugfs \
    51_ultimate_no_mux \
    52_full_cleanup_after_eco_success \
    54_failed_eco_preserves_blocks \
    55_pci_eco_nv_loaded \
    56_pci_eco_already_unloaded \
    57_pci_eco_nv_bound \
    58_pci_eco_rmmod_fail_giveup \
    59_pci_standard_clears_blocks \
    60_pci_standard_no_blocks \
    61_pci_optimized_noop \
    62_pci_ultimate_noop \
    63_pci_no_asus_hardware \
    64_pci_runs_mux_safety \
    65_pci_with_vfio_passthrough \
    66_pci_nouveau_loaded \
    67_backend_garbage \
    68_backend_with_whitespace \
    69_backend_missing \
    70_explicit_asus_wmi \
    71_pci_mux0_with_eco_trigger \
    72_pci_mux0_with_blocks_no_trigger \
    73_pci_mux0_clean_state \
    74_pci_mux0_with_standard_trigger \
    75_pci_mux0_with_legacy_1_trigger \
    76_persistent_eco_no_trigger \
    77_oneshot_overrides_persistent \
    78_persistent_eco_mux0_safety \
    79_persistent_preserves_blocks \
    80_persistent_eco_already_applied \
    81_persistent_eco_pci_backend \
    82_no_trigger_no_persistent \
    83_eco_nvidia_module_not_bound \
    84_eco_bound_driver_defers_no_hang \
    85_persistent_eco_nvidia_bound_defers \
    86_eco_driverless_nvidia_dgpu \
    87_persistent_eco_bound_driver_defers \
    88_mux0_persistent_eco_nvidia_bound \
    89_mux0_oneshot_eco_with_blocks \
    90_mux0_clean_ultimate \
    91_stale_amdgpu_block_stripped \
; do
    scenario "$name"
done

echo "═"
echo "Total: $TESTS_TOTAL   Passed: $TESTS_PASSED   Failed: $TESTS_FAILED"
if (( TESTS_FAILED > 0 )); then
    echo "Failed scenarios:"
    for n in "${FAILED_NAMES[@]}"; do
        echo "  - $n  (sandbox: /tmp/ghelper-test-$n)"
    done
    exit 1
fi
echo "All scenarios passed."
exit 0
