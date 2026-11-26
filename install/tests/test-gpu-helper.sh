#!/usr/bin/env bash
# Argument checks for vendor/gpu-helper, the root helper sudo runs without a
# password. Builds the binary like build.sh and runs it as the current user.
# Only input that must be rejected, or that targets a device that does not
# exist, is sent. Exit codes: 1 rejected, 2 bad signal, 3 accepted but the
# sysfs write failed (proves the argument passed validation).
# ec-fanctl runs in a separate test build against a simulated EC.
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
        process_ops.c nvidia_ops.c pci_ops.c wmi_ops.c ec_ops.c msr_ops.c lenovo_ops.c bpf_ops.c lsm_ops.c -ldl
) || { echo "FATAL: gpu-helper build failed" >&2; exit 2; }

# lsm-enable/lsm-disable test build: paths in $LSM_DIR, no bootloader tool runs.
LSM_DIR="$WORK/lsm"
LSM_BIN="$WORK/gpu-helper-lsm"
mkdir -p "$LSM_DIR"
(
    cd "$SRC_DIR" || exit 1
    cc -O2 -Wall -Wno-unused-result -DNDEBUG -DGH_LSM_TEST="\"$LSM_DIR\"" -o "$LSM_BIN" gpu-helper.c \
        process_ops.c nvidia_ops.c pci_ops.c wmi_ops.c ec_ops.c msr_ops.c lenovo_ops.c bpf_ops.c lsm_ops.c -ldl
) || { echo "FATAL: gpu-helper lsm test build failed" >&2; exit 2; }

# ec-fanctl test build: simulated EC (ec-sim.h, EC_SIM env), files in $EC_DIR.
EC_DIR="$WORK/ec"
EC_BIN="$WORK/gpu-helper-ec"
mkdir -p "$EC_DIR"
(
    cd "$SRC_DIR" || exit 1
    cc -O2 -Wall -Wno-unused-result -DNDEBUG -DGH_EC_SIM="\"$EC_DIR\"" -I"$SCRIPT_DIR" -o "$EC_BIN" gpu-helper.c \
        process_ops.c nvidia_ops.c pci_ops.c wmi_ops.c ec_ops.c msr_ops.c lenovo_ops.c bpf_ops.c lsm_ops.c -ldl
) || { echo "FATAL: gpu-helper ec test build failed" >&2; exit 2; }

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

# -- bpf-block ------------------------------------------------------------------
test_bpf_block_allow_pid_args() {
    expect 1 "usage: bpf-block allow-pid" -- bpf-block allow-pid || return 1
    expect 1 "usage: bpf-block allow-pid" -- bpf-block allow-pid 123 badopt || return 1
    local p
    for p in 1x "" 0 1 -5 99999999; do
        expect 1 "invalid pid" -- bpf-block allow-pid "$p" || return 1
    done
    expect 1 "no such process" -- bpf-block allow-pid 4194303 || return 1
}

test_bpf_block_entry_validation() {
    local e
    for e in "A B=1" "=1" "A=" "A=x/y" "bad name" "a/b" "$(printf 'K=%046d' 0)"; do
        expect 1 "invalid entry" -- bpf-block allow-comm "$e" || return 1
    done
    expect 1 "always allowed" -- bpf-block allow-comm gpu-helper || return 1
}

# -- lsm-enable / lsm-disable ----------------------------------------------------
lsm_run() { OUT="$("$LSM_BIN" "$@" 2>&1)"; RC=$?; }

# Fresh fake system: active LSM list without bpf and a grub defaults file.
lsm_setup() {
    rm -rf "$LSM_DIR" && mkdir -p "$LSM_DIR"
    echo "lockdown,capability,yama,apparmor" > "$LSM_DIR/lsm"
    printf 'GRUB_DEFAULT=0\nGRUB_CMDLINE_LINUX_DEFAULT="%s"\nGRUB_CMDLINE_LINUX=""\n' "$1" > "$LSM_DIR/grub"
}

grub_line() { grep '^GRUB_CMDLINE_LINUX_DEFAULT=' "$LSM_DIR/grub"; }

test_lsm_round_trip_restores_replaced_token() {
    lsm_setup "quiet lsm=landlock,yama splash"
    lsm_run lsm-enable
    [[ $RC -eq 5 ]] || { fail "enable rc=$RC"; return 1; }
    [[ "$(grub_line)" == 'GRUB_CMDLINE_LINUX_DEFAULT="quiet splash lsm=lockdown,capability,yama,apparmor,bpf"' ]] \
        || { fail "enable: $(grub_line)"; return 1; }
    # A parameter the user adds afterwards must survive the revert.
    sed -i 's/bpf"$/bpf extra=1"/' "$LSM_DIR/grub"
    lsm_run lsm-disable
    [[ $RC -eq 5 ]] || { fail "disable rc=$RC"; return 1; }
    [[ "$(grub_line)" == 'GRUB_CMDLINE_LINUX_DEFAULT="quiet splash lsm=landlock,yama extra=1"' ]] \
        || { fail "disable: $(grub_line)"; return 1; }
    [[ ! -e "$LSM_DIR/state" && ! -e "$LSM_DIR/grub.ghelper-bak" ]] || { fail "state or backup left"; return 1; }
    grep -qx 'GRUB_DEFAULT=0' "$LSM_DIR/grub" || { fail "other lines lost"; return 1; }
}

test_lsm_round_trip_drops_added_token() {
    lsm_setup "quiet splash"
    lsm_run lsm-enable
    [[ $RC -eq 5 ]] || { fail "enable rc=$RC"; return 1; }
    lsm_run lsm-disable
    [[ $RC -eq 5 ]] || { fail "disable rc=$RC"; return 1; }
    [[ "$(grub_line)" == 'GRUB_CMDLINE_LINUX_DEFAULT="quiet splash"' ]] || { fail "disable: $(grub_line)"; return 1; }
}

test_lsm_disable_leaves_token_changed_by_user() {
    lsm_setup "quiet"
    lsm_run lsm-enable
    sed -i 's/lsm=[^" ]*/lsm=landlock,bpf/' "$LSM_DIR/grub"
    lsm_run lsm-disable
    [[ $RC -eq 6 ]] || { fail "rc=$RC"; return 1; }
    [[ "$(grub_line)" == 'GRUB_CMDLINE_LINUX_DEFAULT="quiet lsm=landlock,bpf"' ]] || { fail "touched: $(grub_line)"; return 1; }
}

test_lsm_disable_nothing_to_undo() {
    lsm_setup "quiet lsm=landlock,bpf"
    lsm_run lsm-disable
    [[ $RC -eq 0 && "$OUT" == *"lsm=none"* ]] || { fail "rc=$RC"; return 1; }
    [[ "$(grub_line)" == 'GRUB_CMDLINE_LINUX_DEFAULT="quiet lsm=landlock,bpf"' ]] || { fail "touched: $(grub_line)"; return 1; }
}

# -- ec-fanctl ------------------------------------------------------------------
# The simulated EC follows the decompiled ASUS Windows code: AsusSAIO.sys port
# protocol (lpc), the SMBIOS check of the 2026+ ASUS EC driver, AsusEC DLL
# feature report 7 (hid), and the MyASUS fan test rule in AsusSystemDiagnosis.exe.
# ec_run "<link> <version> <vendor bit> <fans> <mode> [fault]" <stdin lines...>
# Replies, EC writes ("w <reg> <value>") and protocol errors ("bad ...") land in OUT.
ec_run() {
    local cfg="$1"
    shift
    OUT="$(printf '%s\n' "$@" | EC_SIM="$cfg" "$EC_BIN" ec-fanctl 2>&1)"
    RC=$?
}

has() { grep -qxF -- "$1" <<<"$OUT" || { fail "missing line: $1"; return 1; }; }
lacks() { ! grep -qxF -- "$1" <<<"$OUT" || { fail "unexpected line: $1"; return 1; }; }
protocol_ok() { ! grep -q '^bad' <<<"$OUT" || { fail "protocol error"; return 1; }; }
last_is() { [[ "$(tail -n 1 <<<"$OUT")" == "$1" ]] || { fail "last line is not: $1"; return 1; }; }

# Fake hidraw node: sysfs uevent, report descriptor and a /dev file.
# hid_dev <name> <VID:PID> <descriptor hex>
hid_dev() {
    mkdir -p "$EC_DIR/sys/$1/device" "$EC_DIR/dev"
    printf 'DRIVER=hid-generic\nHID_ID=0018:0000%s:0000%s\n' "${2%%:*}" "${2##*:}" > "$EC_DIR/sys/$1/device/uevent"
    printf "$(sed 's/../\\x&/g' <<<"$3")" > "$EC_DIR/sys/$1/device/report_descriptor"
    : > "$EC_DIR/dev/$1"
}
hid_reset() { rm -rf "$EC_DIR/sys" "$EC_DIR/dev"; }

# SMBIOS table the lpc link reads: dmi <strings...>
dmi() { printf '%s\0' "$@" > "$EC_DIR/dmi"; }
dmi "ASUSTeK COMPUTER INC."

# Usage Page (0xFF32), Usage (0x84), Collection: what the AsusEC DLL matches.
EC_DESC="0632ff0984a101850709011500260000750895403f00b102c0"

test_ec_detect_myasus_rule() {
    local cfg want
    while IFS='|' read -r cfg want; do
        ec_run "lpc $cfg" probe
        has "$want" || return 1
    done <<'CASES'
18 1 2 0|probe ok lpc 18 2 1000 1000
2 1 2 0|probe ok lpc 2 2 1000 1000
1 1 2 0|probe no version
0 1 2 0|probe no version
255 1 2 0|probe no silent
4 0 2 0|probe no odm
4 1 2 0|probe ok lpc 4 2 1000 1000
5 0 2 0|probe ok lpc 5 2 1000 1000
18 1 0 0|probe no fans
18 1 3 0|probe ok lpc 18 3 1000 1000 1000
18 1 4 0|probe no fans
CASES
}

test_ec_lpc_port_protocol() {
    ec_run "lpc 18 1 2 0" probe "set 100 120" "set 100 130" quit
    protocol_ok && has "w 31 01" && has "w 35 64" && has "w 35 78" && has "w 35 82" || return 1
    [[ "$(grep -c '^w 35 64$' <<<"$OUT")" == 1 ]] || { fail "unchanged duty rewritten"; return 1; }
    last_is "w 31 00"
}

test_ec_lpc_drains_stale_output() {
    ec_run "lpc 18 1 2 0 1" probe "set 50 50" quit
    protocol_ok && has "probe ok lpc 18 2 1000 1000"
}

test_ec_select_before_duty() {
    ec_run "lpc 18 1 2 0" probe "set 64 96" quit
    grep -A1 -x 'w 32 00' <<<"$OUT" | grep -qx 'w 35 40' || { fail "fan 0: select then duty"; return 1; }
    grep -A1 -x 'w 32 01' <<<"$OUT" | grep -qx 'w 35 60' || { fail "fan 1: select then duty"; return 1; }
}

test_ec_duty_bounds() {
    # MyASUS fan test duties: 0x4C (30 %), 0x59 (35 %), 0x99 (60 %), 0xFF
    ec_run "lpc 18 1 2 0" probe "set 0 255" "set 76 89" "set 153 255" "set 256" "set 0 -1" quit
    [[ "$(grep -cx ok <<<"$OUT")" == 3 && "$(grep -cx 'err range' <<<"$OUT")" == 2 ]] || { fail "bounds"; return 1; }
    has "w 35 00" && has "w 35 ff" && has "w 35 4c" && has "w 35 59" && has "w 35 99"
}

test_ec_hid_feature_report() {
    hid_reset
    hid_dev hidraw3 0B05:4543 "$EC_DESC"
    ec_run "hid 18 1 2 0" probe "set 100 120" quit
    hid_reset
    protocol_ok && has "probe ok hid 18 2 1000 1000" && has "ok" && has "w 35 78" || return 1
    last_is "w 31 00"
}

test_ec_hid_needs_asus_ec_device() {
    hid_reset
    hid_dev hidraw1 0B05:19B6 "$EC_DESC"
    hid_dev hidraw2 0B05:4543 "05010906a101c0"
    ec_run "hid 18 1 2 0" probe "set 50 50"
    hid_reset
    has "probe no silent" && has "err probe" && lacks "w 31 01"
}

test_ec_hid_error_status() {
    hid_reset
    hid_dev hidraw3 0B05:4543 "$EC_DESC"
    ec_run "hid 18 1 2 0 2" probe
    hid_reset
    has "probe no silent"
}

test_ec_lpc_before_hid() {
    hid_reset
    hid_dev hidraw3 0B05:4543 "$EC_DESC"
    ec_run "both 18 1 2 0" probe
    hid_reset
    has "probe ok lpc 18 2 1000 1000"
}

test_ec_lpc_needs_asus_smbios() {
    local vendor
    for vendor in "ASUSTeK COMPUTER INC." "ASUSTeK Computer Inc." "LENOVO"; do
        dmi "$vendor"
        ec_run "lpc 18 1 2 0" probe "set 50 50"
        dmi "ASUSTeK COMPUTER INC."
        if [[ "$vendor" == ASUSTeK* ]]; then
            has "probe ok lpc 18 2 1000 1000" || return 1
        else
            has "probe no nolink" && has "err probe" && lacks "w 31 01" || return 1
        fi
    done
}

test_ec_no_link() {
    ec_run "none 18 1 2 0" probe "set 50 50"
    has "probe no silent" && has "err probe" && lacks "w 31 01"
}

test_ec_eof_releases_even_if_manual_at_start() {
    ec_run "lpc 18 1 2 1" probe "set 50 50"
    lacks "w 31 01" && last_is "w 31 00"
}

test_ec_refused_release_runs_fans_full() {
    ec_run "lpc 18 1 2 0 3" probe "set 50 60" quit
    [[ "$(tail -n 5 <<<"$OUT")" == $'w 31 00\nw 32 00\nw 35 ff\nw 32 01\nw 35 ff' ]] || { fail "no full duty after refused release"; return 1; }
}

test_ec_rearms_after_ec_reset() {
    ec_run "lpc 18 1 2 0" probe "set 50 60" "sim reset" "set 50 60" quit
    [[ "$(grep -cx 'w 31 01' <<<"$OUT")" == 2 ]] || { fail "manual mode not re-armed"; return 1; }
    [[ "$(grep -cx 'w 35 32' <<<"$OUT")" == 2 ]] || { fail "duty not rewritten after reset"; return 1; }
}

test_ec_rejects_bad_input() {
    ec_run "lpc 18 1 2 0" "set 10" probe "set 1 2 3" "set 300" "set -1" bogus quit
    [[ "$(head -n 1 <<<"$OUT")" == "err probe" ]] || { fail "set before probe"; return 1; }
    [[ "$(grep -c '^err range$' <<<"$OUT")" == 3 ]] || { fail "range"; return 1; }
    has "err cmd" && lacks "w 31 01"
}

test_ec_watchdog_releases_then_set_retakes() {
    OUT="$( (printf 'probe\nset 50 60\n'; sleep 7; printf 'set 70 60\nquit\n') \
        | EC_SIM="lpc 18 1 2 0" "$EC_BIN" ec-fanctl 2>&1)"
    [[ "$(grep -cx 'w 31 00' <<<"$OUT")" == 2 ]] || { fail "watchdog + quit releases"; return 1; }
    [[ "$(grep -cx 'w 31 01' <<<"$OUT")" == 2 ]] || { fail "set after watchdog did not retake"; return 1; }
}

test_ec_signal_releases() {
    local fifo="$EC_DIR/in" log="$EC_DIR/out" pid
    rm -f "$fifo" && mkfifo "$fifo"
    EC_SIM="lpc 18 1 2 0" "$EC_BIN" ec-fanctl < "$fifo" > "$log" 2>&1 &
    pid=$!
    exec 7> "$fifo"
    printf 'probe\nset 50 50\n' >&7
    sleep 1
    kill -TERM "$pid"
    wait "$pid"
    exec 7>&-
    OUT="$(cat "$log")"
    has "ok" && last_is "w 31 00"
}

test_ec_second_controller_busy() {
    flock "$EC_DIR/ec-fan.lock" sleep 3 &
    local holder=$!
    sleep 0.5
    ec_run "lpc 18 1 2 0" probe "set 50 50"
    kill "$holder" 2>/dev/null
    wait "$holder" 2>/dev/null
    has "probe no busy" && has "err probe" && lacks "w 31 01"
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
    bpf_block_allow_pid_args \
    bpf_block_entry_validation \
    lsm_round_trip_restores_replaced_token \
    lsm_round_trip_drops_added_token \
    lsm_disable_leaves_token_changed_by_user \
    lsm_disable_nothing_to_undo \
    ec_detect_myasus_rule \
    ec_lpc_port_protocol \
    ec_lpc_drains_stale_output \
    ec_select_before_duty \
    ec_duty_bounds \
    ec_hid_feature_report \
    ec_hid_needs_asus_ec_device \
    ec_hid_error_status \
    ec_lpc_before_hid \
    ec_lpc_needs_asus_smbios \
    ec_no_link \
    ec_eof_releases_even_if_manual_at_start \
    ec_refused_release_runs_fans_full \
    ec_rearms_after_ec_reset \
    ec_rejects_bad_input \
    ec_watchdog_releases_then_set_retakes \
    ec_signal_releases \
    ec_second_controller_busy \
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
