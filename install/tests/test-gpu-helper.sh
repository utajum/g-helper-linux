#!/usr/bin/env bash
# Argument checks for vendor/gpu-helper, the root helper sudo runs without a
# password. Builds the binary like build.sh and runs it as the current user.
# Only input that must be rejected, or that targets a device that does not
# exist, is sent. Exit codes: 1 rejected, 2 bad signal, 3 accepted but the
# sysfs write failed (proves the argument passed validation).
#
# Run:    bash install/tests/test-gpu-helper.sh
# Single: bash install/tests/test-gpu-helper.sh <pattern>
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$(cd "$SCRIPT_DIR/../../vendor/gpu-helper" && pwd)"
PATTERN="${1:-}"

if [[ $EUID -eq 0 ]]; then
    echo "FATAL: do not run as root; accepted arguments would touch real hardware" >&2
    exit 2
fi
if ! command -v cc > /dev/null; then
    echo "FATAL: cc not found" >&2
    exit 2
fi

WORK="$(mktemp -d /tmp/ghelper-gpu-helper-test.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
BIN="$WORK/gpu-helper"
(
    cd "$SRC_DIR" || exit 1
    cc -O2 -Wall -Wno-unused-result -DNDEBUG -o "$BIN" gpu-helper.c \
        process_ops.c nvidia_ops.c pci_ops.c wmi_ops.c ec_ops.c msr_ops.c lenovo_ops.c -ldl
) || { echo "FATAL: gpu-helper build failed" >&2; exit 2; }

# A BDF and a slot that exist on no machine, so accepted writes fail with ENOENT.
NO_BDF="0000:fe:1f.7"
NO_SLOT="ghelper-test-none"

TESTS_TOTAL=0
TESTS_PASSED=0
TESTS_FAILED=0
FAILED_NAMES=()
RC=0
OUT=""

run() { OUT="$("$BIN" "$@" 2>&1)"; RC=$?; }

fail() { echo "  ASSERT FAIL: $*"; [[ -n "$OUT" ]] && sed 's/^/    out: /' <<<"$OUT"; return 1; }

# expect <code> <stderr regex or ""> -- <args...>
expect() {
    local code="$1" msg="$2"
    shift 3
    run "$@"
    [[ "$RC" == "$code" ]] || { fail "gpu-helper $* -> exit $RC, expected $code"; return 1; }
    if [[ -n "$msg" ]]; then
        grep -qE -- "$msg" <<<"$OUT" || { fail "gpu-helper $* -> output should match /$msg/"; return 1; }
    fi
}

scenario() {
    local name="$1"
    if [[ -n "$PATTERN" ]] && [[ "$name" != *"$PATTERN"* ]]; then
        return
    fi
    TESTS_TOTAL=$((TESTS_TOTAL + 1))
    echo "> $name"
    if "test_$name"; then
        echo "  PASS"
        TESTS_PASSED=$((TESTS_PASSED + 1))
    else
        echo "  FAIL"
        TESTS_FAILED=$((TESTS_FAILED + 1))
        FAILED_NAMES+=("$name")
    fi
}

BAD_BDFS=(
    ""
    "0000:01:00.0/"
    "0000:01:00"
    "0000:01:00.00"
    "0000-01:00.0"
    "0000:01-00.0"
    "0000:01:00:0"
    "000g:01:00.0"
    "0000:01:0z.0"
    "../../../../"
    "0000:01:00.0;"
    " 0000:01:00.0"
    "0000:01:00.0 "
)

# -- pci-unbind / pci-bind ------------------------------------------------------
test_pci_unbind_bind_usage() {
    local a
    for a in pci-unbind pci-bind; do
        expect 1 "usage: $a" -- "$a" || return 1
        expect 1 "usage: $a" -- "$a" nvidia || return 1
        expect 1 "usage: $a" -- "$a" nvidia "$NO_BDF" extra || return 1
    done
}

test_pci_unbind_bind_driver_whitelist() {
    local a d
    for a in pci-unbind pci-bind; do
        for d in i915 nouveau xhci_hcd vfio-pci "" "../nvidia" "nvidia/.." NVIDIA "nvidia " amdgpu2; do
            expect 1 "driver not permitted" -- "$a" "$d" "$NO_BDF" || return 1
        done
    done
}

test_pci_unbind_bind_bad_bdf() {
    local a b
    for a in pci-unbind pci-bind; do
        for b in "${BAD_BDFS[@]}"; do
            expect 1 "invalid bdf" -- "$a" nvidia "$b" || return 1
        done
    done
}

test_pci_unbind_bind_allowed_drivers_pass_validation() {
    local a d
    for a in pci-unbind pci-bind; do
        for d in nvidia snd_hda_intel amdgpu; do
            expect 3 "" -- "$a" "$d" "$NO_BDF" || return 1
        done
    done
}

# -- pci-remove -----------------------------------------------------------------
test_pci_remove_usage() {
    expect 1 "usage: pci-remove" -- pci-remove || return 1
    expect 1 "usage: pci-remove" -- pci-remove "$NO_BDF" extra
}

test_pci_remove_bad_bdf() {
    local b
    for b in "${BAD_BDFS[@]}"; do
        expect 1 "invalid bdf" -- pci-remove "$b" || return 1
    done
}

test_pci_remove_valid_bdf_passes_validation() {
    expect 3 "pci-remove: open /sys/bus/pci/devices/$NO_BDF/remove" -- pci-remove "$NO_BDF"
}

# -- slot-power -----------------------------------------------------------------
test_slot_power_usage() {
    expect 1 "usage: slot-power" -- slot-power || return 1
    expect 1 "usage: slot-power" -- slot-power 1 || return 1
    expect 1 "usage: slot-power" -- slot-power 1 1 extra
}

test_slot_power_bad_slot() {
    local s
    for s in "" "." ".." "../1" "1/../2" "a.b" "a_b" "a b" "1;" "$(printf 'a%.0s' {1..33})"; do
        expect 1 "invalid slot" -- slot-power "$s" 1 || return 1
    done
}

test_slot_power_bad_value() {
    local v
    for v in "" 2 -1 on off "1 " 01 true; do
        expect 1 "value must be 0 or 1" -- slot-power "$NO_SLOT" "$v" || return 1
    done
}

test_slot_power_valid_passes_validation() {
    expect 3 "slot-power: open /sys/bus/pci/slots/$NO_SLOT/power" -- slot-power "$NO_SLOT" 0 || return 1
    expect 3 "" -- slot-power "$NO_SLOT" 1 || return 1
    expect 3 "" -- slot-power "$(printf 'a%.0s' {1..32})" 1
}

# -- pci-power ------------------------------------------------------------------
test_pci_power_usage() {
    expect 1 "usage: pci-power" -- pci-power "$NO_BDF" || return 1
    expect 1 "usage: pci-power" -- pci-power "$NO_BDF" auto extra
}

test_pci_power_bad_bdf() {
    local b
    for b in "${BAD_BDFS[@]}"; do
        expect 1 "invalid bdf" -- pci-power "$b" auto || return 1
    done
}

test_pci_power_bad_value() {
    local v
    for v in "" off suspend AUTO "auto " 0 1; do
        expect 1 "value must be auto or on" -- pci-power "$NO_BDF" "$v" || return 1
    done
}

test_pci_power_valid_passes_validation() {
    expect 3 "pci-power: open /sys/bus/pci/devices/$NO_BDF/power/control" -- pci-power "$NO_BDF" auto || return 1
    expect 3 "" -- pci-power "$NO_BDF" on
}

# -- vulkan-icd / egl-vendor ----------------------------------------------------
test_vulkan_icd_args() {
    local v
    expect 1 "usage: vulkan-icd" -- vulkan-icd || return 1
    expect 1 "usage: vulkan-icd" -- vulkan-icd hide extra || return 1
    for v in "" HIDE delete /tmp/x ../x; do
        expect 1 "arg must be hide or show" -- vulkan-icd "$v" || return 1
    done
}

test_egl_vendor_args() {
    local v
    expect 1 "usage: egl-vendor" -- egl-vendor || return 1
    expect 1 "usage: egl-vendor" -- egl-vendor show extra || return 1
    for v in "" SHOW delete /tmp/x ../x; do
        expect 1 "arg must be hide or show" -- egl-vendor "$v" || return 1
    done
}

# -- drm-notify-remove ----------------------------------------------------------
test_drm_notify_remove_args() {
    local b
    expect 1 "usage: drm-notify-remove" -- drm-notify-remove || return 1
    expect 1 "usage: drm-notify-remove" -- drm-notify-remove "$NO_BDF" extra || return 1
    for b in "${BAD_BDFS[@]}"; do
        expect 1 "invalid bdf" -- drm-notify-remove "$b" || return 1
    done
}

# -- daemon ---------------------------------------------------------------------
test_daemon_usage() {
    expect 1 "usage: daemon" -- daemon || return 1
    expect 1 "usage: daemon" -- daemon stop || return 1
    expect 1 "usage: daemon" -- daemon stop nvidia-powerd extra
}

test_daemon_verb_whitelist() {
    local v
    for v in restart enable disable mask kill reload "" STOP "stop;"; do
        expect 1 "verb not permitted" -- daemon "$v" nvidia-powerd || return 1
    done
}

test_daemon_unit_whitelist() {
    local u
    for u in sshd nvidia-powerd.service nvidia-persistenced.service display-manager "" "../nvidia-powerd" "nvidia-powerd;x" NVIDIA-POWERD; do
        expect 1 "unit not permitted" -- daemon stop "$u" || return 1
    done
}

# -- rmmod ----------------------------------------------------------------------
test_rmmod_usage() {
    expect 1 "usage: rmmod" -- rmmod || return 1
    expect 1 "usage: rmmod" -- rmmod nvidia -f
}

test_rmmod_module_whitelist() {
    local m
    for m in i915 amdgpu nouveau snd_hda_intel "" -f --force "nvidia;x" "../nvidia" NVIDIA nvidia-drm; do
        expect 1 "module not permitted" -- rmmod "$m" || return 1
    done
}

# -- modprobe -------------------------------------------------------------------
test_modprobe_whitelist() {
    expect 1 "arguments not permitted" -- modprobe || return 1
    local m
    for m in i915 nouveau nvidia_drm "" -a --force "nvidia;x" "../nvidia" NVIDIA; do
        expect 1 "arguments not permitted" -- modprobe "$m" || return 1
    done
    expect 1 "arguments not permitted" -- modprobe -r nvidia || return 1
    expect 1 "arguments not permitted" -- modprobe -r amdgpu || return 1
    expect 1 "arguments not permitted" -- modprobe nvidia NVreg_x=1 || return 1
    expect 1 "arguments not permitted" -- modprobe amdgpu runpm=0 || return 1
    expect 1 "arguments not permitted" -- modprobe -r uvcvideo extra
}

# -- smi ------------------------------------------------------------------------
test_smi_flag_whitelist() {
    expect 1 "usage: smi" -- smi || return 1
    local f
    for f in -r --gpu-reset -e -pm -c -q -i "" -PL "-pl;x"; do
        expect 1 "flag not permitted" -- smi "$f" || return 1
    done
}

test_smi_too_many_args() {
    expect 1 "too many args" -- smi -pl 100 -i || return 1
    expect 1 "too many args" -- smi -lgc 300,1500 extra
}

# -- nvml-clocks ----------------------------------------------------------------
test_nvml_clocks_usage() {
    expect 1 "usage: nvml-clocks" -- nvml-clocks || return 1
    expect 1 "usage: nvml-clocks" -- nvml-clocks 0 || return 1
    expect 1 "usage: nvml-clocks" -- nvml-clocks 0 0 0
}

test_nvml_clocks_not_integer() {
    local v
    for v in abc 1.5 10x 0x10 " 1x"; do
        expect 1 "core: not an integer" -- nvml-clocks "$v" 0 || return 1
        expect 1 "mem: not an integer" -- nvml-clocks 0 "$v" || return 1
    done
}

test_nvml_clocks_range() {
    expect 1 "core offset 3001 out of range" -- nvml-clocks 3001 0 || return 1
    expect 1 "core offset -3001 out of range" -- nvml-clocks -3001 0 || return 1
    expect 1 "mem offset 10001 out of range" -- nvml-clocks 0 10001 || return 1
    expect 1 "mem offset -10001 out of range" -- nvml-clocks 0 -10001 || return 1
    expect 1 "out of range" -- nvml-clocks 99999999999999999999 0
}

# -- kill -----------------------------------------------------------------------
test_kill_usage() {
    expect 1 "usage: kill" -- kill || return 1
    expect 1 "usage: kill" -- kill -15
}

test_kill_signal_whitelist() {
    local s
    for s in -1 -2 -19 HUP SIGSTOP -STOP 0 "" -15x; do
        expect 2 "only -15 and -9" -- kill "$s" 999999999 || return 1
    done
}

test_kill_skips_pid_0() {
    # kill(0) would signal the caller's whole process group; run in a new one.
    OUT="$(setsid -w "$BIN" kill -15 0 2>&1)"
    RC=$?
    [[ "$RC" == 0 ]] || fail "kill -15 0 -> exit $RC, expected 0 (pid 0 must be skipped)"
}

# -- raw WMI --------------------------------------------------------------------
test_wmi_dsts_usage() {
    expect 1 "usage: wmi-dsts" -- wmi-dsts || return 1
    expect 1 "usage: wmi-dsts" -- wmi-dsts 0x00090020 extra
}

test_wmi_dsts_devid_whitelist() {
    local d
    for d in 0 0x00090021 0x00120057 0x00050021 0x00110013 abc "" -1 0x100090020; do
        expect 1 "devid not permitted" -- wmi-dsts "$d" || return 1
    done
}

test_wmi_dsts_gpu_devids_pass_validation() {
    local d
    for d in 0x00090020 0x00090120 0x00090016 0x00090026 0x00090018 0x00090019 0x00120099; do
        expect 3 "" -- wmi-dsts "$d" || return 1
    done
}

test_wmi_devs_usage() {
    expect 1 "usage: wmi-devs" -- wmi-devs || return 1
    expect 1 "usage: wmi-devs" -- wmi-devs 0x00090020 || return 1
    expect 1 "usage: wmi-devs" -- wmi-devs 0x00090020 1 extra
}

test_wmi_devs_devid_whitelist() {
    local d
    for d in 0 0x00090021 0x00120057 0x00050021 0x00110013 abc -1; do
        expect 1 "devid not permitted" -- wmi-devs "$d" 1 || return 1
    done
}

test_wmi_devs_ctrl_not_integer() {
    local v
    for v in abc 1x "1 " 0x1g "1;2"; do
        expect 1 "ctrl_param not an integer" -- wmi-devs 0x00090020 "$v" || return 1
    done
}

# -- list / dispatch ------------------------------------------------------------
# Format NvidiaProcessScanner parses: pid fd uid comm libs unit dri i2c.
test_list_output_format() {
    expect 0 "" -- list || return 1
    local line pid fd uid comm libs unit dri i2c
    while IFS= read -r line; do
        [[ -z "$line" ]] && continue
        [[ "$(awk -F'\t' '{print NF}' <<<"$line")" == 8 ]] || { fail "list line without 8 fields: $line"; return 1; }
        IFS=$'\t' read -r pid fd uid comm libs unit dri i2c <<<"$line"
        [[ "$pid$fd$uid$dri$i2c" =~ ^[0-9]+$ ]] || { fail "non-numeric count in: $line"; return 1; }
        [[ "$libs" == 0 || "$libs" == 1 ]] || { fail "libs not 0/1 in: $line"; return 1; }
        [[ -n "$comm" && -n "$unit" ]] || { fail "empty comm/unit in: $line"; return 1; }
    done <<<"$OUT"
}

test_no_args_lists() {
    expect 0 "" --
}

test_unknown_subcommand_lists() {
    expect 0 "" -- frobnicate
}

# -- Run ------------------------------------------------------------------------
echo "="
echo " gpu-helper argument tests"
echo "="

for name in \
    pci_unbind_bind_usage \
    pci_unbind_bind_driver_whitelist \
    pci_unbind_bind_bad_bdf \
    pci_unbind_bind_allowed_drivers_pass_validation \
    pci_remove_usage \
    pci_remove_bad_bdf \
    pci_remove_valid_bdf_passes_validation \
    slot_power_usage \
    slot_power_bad_slot \
    slot_power_bad_value \
    slot_power_valid_passes_validation \
    pci_power_usage \
    pci_power_bad_bdf \
    pci_power_bad_value \
    pci_power_valid_passes_validation \
    vulkan_icd_args \
    egl_vendor_args \
    drm_notify_remove_args \
    daemon_usage \
    daemon_verb_whitelist \
    daemon_unit_whitelist \
    rmmod_usage \
    rmmod_module_whitelist \
    modprobe_whitelist \
    smi_flag_whitelist \
    smi_too_many_args \
    nvml_clocks_usage \
    nvml_clocks_not_integer \
    nvml_clocks_range \
    kill_usage \
    kill_signal_whitelist \
    kill_skips_pid_0 \
    wmi_dsts_usage \
    wmi_dsts_devid_whitelist \
    wmi_dsts_gpu_devids_pass_validation \
    wmi_devs_usage \
    wmi_devs_devid_whitelist \
    wmi_devs_ctrl_not_integer \
    list_output_format \
    no_args_lists \
    unknown_subcommand_lists \
; do
    scenario "$name"
done

echo "="
echo "Total: $TESTS_TOTAL   Passed: $TESTS_PASSED   Failed: $TESTS_FAILED"
if (( TESTS_FAILED > 0 )); then
    echo "Failed scenarios:"
    for n in "${FAILED_NAMES[@]}"; do
        echo "  - $n"
    done
    exit 1
fi
echo "All scenarios passed."
exit 0
