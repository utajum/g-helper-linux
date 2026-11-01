#!/usr/bin/env bash
# Tests for install/gpu-block-helper.sh.
#
# The helper has fixed /etc and /sys paths and runs as root in production.
# Each run uses a copy with those paths and PATH redirected into a sandbox,
# with udevadm/modprobe stubs that record their calls. The copy is checked so
# a new unredirected path fails the suite instead of touching the host.
# Also checks that the helper, the boot script and the app agree on paths.
#
# Run:    bash install/tests/test-gpu-block-helper.sh
# Single: bash install/tests/test-gpu-block-helper.sh <pattern>
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
INSTALL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_DIR="$(cd "$INSTALL_DIR/.." && pwd)"
HELPER="$INSTALL_DIR/gpu-block-helper.sh"
BOOT_SCRIPT="$INSTALL_DIR/ghelper-gpu-boot.sh"
PATTERN="${1:-}"

if [[ $EUID -eq 0 ]]; then
    echo "FATAL: do not run as root; a broken redirect would write to the real /etc" >&2
    exit 2
fi

WORK="$(mktemp -d /tmp/ghelper-block-helper-test.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
COPY="$WORK/gpu-block-helper.sh"
STUBS="$WORK/stubs"

# -- Sandboxed copy -------------------------------------------------------------
src="$(cat "$HELPER")"
redirect() {
    local n
    n=$(grep -cF -- "$1" <<<"$src")
    if [[ "$n" != 1 ]]; then
        echo "FATAL: expected one '$1' in gpu-block-helper.sh, found $n" >&2
        exit 2
    fi
    src="${src/"$1"/"$2"}"
}
redirect 'PATH=/usr/sbin:/usr/bin:/sbin:/bin' 'PATH="$GH_STUBS:/usr/sbin:/usr/bin:/sbin:/bin"'
redirect 'MODPROBE_DEST="/etc/modprobe.d/ghelper-gpu-block.conf"' 'MODPROBE_DEST="$GH_ROOT/etc/modprobe.d/ghelper-gpu-block.conf"'
redirect 'UDEV_DEST="/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"' 'UDEV_DEST="$GH_ROOT/etc/udev/rules.d/50-ghelper-remove-dgpu.rules"'
redirect 'TRIGGER_DIR="/etc/ghelper"' 'TRIGGER_DIR="$GH_ROOT/etc/ghelper"'
redirect 'echo 1 > /sys/bus/pci/rescan' 'echo 1 > "$GH_ROOT/sys/bus/pci/rescan"'
printf '%s\n' "$src" > "$COPY"

leftover=$(sed 's#/dev/null##g' "$COPY" | grep -nE '(^|[^A-Za-z0-9_}])/(etc|sys|proc|dev)/' | grep -vE '^[0-9]+:[[:space:]]*#')
if [[ -n "$leftover" ]]; then
    echo "FATAL: gpu-block-helper.sh has paths the test does not redirect:" >&2
    echo "$leftover" >&2
    exit 2
fi

mkdir -p "$STUBS"
for tool in udevadm modprobe; do
    cat > "$STUBS/$tool" << 'STUB'
#!/bin/bash
echo "$(basename "$0") $*" >> "$GH_ROOT/calls"
exit "${GH_STUB_EXIT:-0}"
STUB
    chmod +x "$STUBS/$tool"
done

# -- Results --------------------------------------------------------------------
TESTS_TOTAL=0
TESTS_PASSED=0
TESTS_FAILED=0
FAILED_NAMES=()
SB=""
RC=0

new_sandbox() {
    SB="$WORK/sb-${1//[^A-Za-z0-9_-]/_}"
    rm -rf "$SB"
    mkdir -p "$SB/etc/modprobe.d" "$SB/etc/udev/rules.d" "$SB/sys/bus/pci"
    : > "$SB/sys/bus/pci/rescan"
    : > "$SB/calls"
}

MODPROBE=/etc/modprobe.d/ghelper-gpu-block.conf
UDEV=/etc/udev/rules.d/50-ghelper-remove-dgpu.rules
TRIGGER=/etc/ghelper/pending-gpu-mode
PERSISTENT=/etc/ghelper/persistent-gpu-mode
BACKEND=/etc/ghelper/backend

# Run the helper copy. Exit code in RC, output in $SB/out.
helper() {
    GH_ROOT="$SB" GH_STUBS="$STUBS" bash "$COPY" "$@" > "$SB/out" 2>&1
    RC=$?
}

put() { mkdir -p "$(dirname "$SB$1")"; echo "$2" > "$SB$1"; }

fail() { echo "  ASSERT FAIL: $*"; [[ -s "$SB/out" ]] && sed 's/^/    out: /' "$SB/out"; return 1; }
expect_rc()      { [[ "$RC" == "$1" ]] || fail "exit code $RC, expected $1"; }
expect_exists()  { [[ -f "$SB$1" ]] || fail "$1 should exist"; }
expect_missing() { [[ ! -e "$SB$1" ]] || fail "$1 should NOT exist"; }
expect_content() {
    [[ -f "$SB$1" ]] || { fail "$1 missing"; return 1; }
    local actual
    actual="$(tr -d '[:space:]' < "$SB$1")"
    [[ "$actual" == "$2" ]] || fail "$1 contains '$actual', expected '$2'"
}
expect_mode()     { local m; m=$(stat -c %a "$SB$1"); [[ "$m" == "$2" ]] || fail "$1 mode $m, expected $2"; }
expect_out()      { grep -qE -- "$1" "$SB/out" || fail "output should match /$1/"; }
expect_calls()    { local c; c="$(cat "$SB/calls")"; [[ "$c" == "$1" ]] || fail "calls were '$c', expected '$1'"; }
expect_no_files() {
    local f
    f=$(cd "$SB" && find etc -type f | sort | tr '\n' ' ')
    [[ -z "$f" ]] || fail "no files expected, found: $f"
}

scenario() {
    local name="$1"
    if [[ -n "$PATTERN" ]] && [[ "$name" != *"$PATTERN"* ]]; then
        return
    fi
    TESTS_TOTAL=$((TESTS_TOTAL + 1))
    new_sandbox "$name"
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

# Non-comment lines of the generated udev rule.
udev_rules() { grep -vE '^[[:space:]]*(#|$)' "$SB$UDEV"; }

# -- write ----------------------------------------------------------------------
test_write_eco_asus_wmi() {
    helper write eco asus-wmi
    expect_rc 0 || return 1
    expect_content $TRIGGER eco || return 1
    expect_content $BACKEND asus-wmi || return 1
    expect_exists $MODPROBE || return 1
    expect_exists $UDEV || return 1
    expect_calls "" || return 1
}

test_write_eco_pci() {
    helper write eco pci
    expect_rc 0 || return 1
    expect_content $TRIGGER eco || return 1
    expect_content $BACKEND pci || return 1
    expect_exists $MODPROBE || return 1
    expect_exists $UDEV || return 1
}

test_write_defaults_eco_asus_wmi() {
    helper write
    expect_rc 0 || return 1
    expect_content $TRIGGER eco || return 1
    expect_content $BACKEND asus-wmi || return 1
    expect_exists $MODPROBE || return 1
}

test_write_creates_etc_ghelper() {
    [[ ! -d "$SB/etc/ghelper" ]] || { fail "precondition"; return 1; }
    helper write standard pci
    expect_rc 0 || return 1
    expect_content $TRIGGER standard || return 1
}

test_write_file_modes() {
    helper write eco pci
    expect_mode $MODPROBE 644 || return 1
    expect_mode $UDEV 644 || return 1
    expect_mode $BACKEND 644 || return 1
}

test_write_standard_removes_blocks() {
    helper write eco asus-wmi
    helper write standard asus-wmi
    expect_rc 0 || return 1
    expect_missing $MODPROBE || return 1
    expect_missing $UDEV || return 1
    expect_content $TRIGGER standard || return 1
}

test_write_optimized_no_blocks() {
    helper write eco pci
    helper write optimized pci
    expect_rc 0 || return 1
    expect_content $TRIGGER optimized || return 1
    expect_missing $MODPROBE || return 1
    expect_missing $UDEV || return 1
}

test_write_ultimate_no_blocks() {
    helper write eco asus-wmi
    helper write ultimate asus-wmi
    expect_rc 0 || return 1
    expect_content $TRIGGER ultimate || return 1
    expect_missing $MODPROBE || return 1
}

test_write_overwrites_trigger_and_backend() {
    helper write eco asus-wmi
    helper write standard pci
    expect_content $TRIGGER standard || return 1
    expect_content $BACKEND pci || return 1
}

test_write_keeps_persistent_marker() {
    put $PERSISTENT eco
    helper write standard asus-wmi
    expect_rc 0 || return 1
    expect_content $PERSISTENT eco || return 1
}

test_write_invalid_mode_rejected() {
    for mode in turbo ECO Eco "eco;x" "../eco" "eco standard"; do
        new_sandbox "write_invalid_mode"
        helper write "$mode" asus-wmi
        [[ "$RC" == 1 ]] || { fail "mode '$mode' exit $RC, expected 1"; return 1; }
        expect_no_files || return 1
    done
}

test_write_invalid_backend_rejected() {
    for backend in nvidia PCI "pci;x" "../pci"; do
        new_sandbox "write_invalid_backend"
        helper write eco "$backend"
        [[ "$RC" == 1 ]] || { fail "backend '$backend' exit $RC, expected 1"; return 1; }
        expect_no_files || return 1
    done
}

test_write_eco_modprobe_blocks_all_dgpu_modules() {
    helper write eco asus-wmi
    local mods
    mods=$(grep -vE '^[[:space:]]*(#|$)' "$SB$MODPROBE" | sort | tr '\n' '|')
    local want="install amdgpu /bin/false|install nouveau /bin/false|install nvidia /bin/false|install nvidia_drm /bin/false|install nvidia_modeset /bin/false|install nvidia_uvm /bin/false|install nvidia_wmi_ec_backlight /bin/false|"
    [[ "$mods" == "$want" ]] || fail "modprobe block lines: $mods"
}

test_write_eco_udev_rules_shape() {
    helper write eco asus-wmi
    local n=0 line
    while IFS= read -r line; do
        n=$((n + 1))
        [[ "$line" == 'ACTION=="add", SUBSYSTEM=="pci", '* ]] || { fail "rule not add/pci: $line"; return 1; }
        [[ "$line" == *'ATTR{remove}="1"' ]] || { fail "rule does not end with remove: $line"; return 1; }
        [[ "$line" == *'ATTR{power/control}="auto"'* ]] || { fail "rule without power/control: $line"; return 1; }
        [[ "$line" =~ ATTR\{vendor\}==\"0x(10de|1002)\" ]] || { fail "rule for unexpected vendor: $line"; return 1; }
    done < <(udev_rules)
    [[ "$n" == 8 ]] || fail "expected 8 rules, found $n"
}

test_write_eco_udev_display_rules_keep_boot_vga() {
    helper write eco asus-wmi
    local line
    while IFS= read -r line; do
        if [[ "$line" =~ ATTR\{class\}==\"0x030[02]00\" ]]; then
            [[ "$line" == *'ATTR{boot_vga}!="1"'* ]] || { fail "display rule can remove the boot GPU: $line"; return 1; }
        fi
    done < <(udev_rules)
}

test_write_eco_udev_class_set() {
    helper write eco asus-wmi
    local got
    got=$(udev_rules | sed -E 's/.*ATTR\{vendor\}=="([^"]+)".*ATTR\{class\}=="([^"]+)".*/\1:\2/' | sort | tr '\n' ' ')
    local want="0x1002:0x030000 0x1002:0x030200 0x1002:0x040300 0x10de:0x030000 0x10de:0x030200 0x10de:0x040300 0x10de:0x0c0330 0x10de:0x0c8000 "
    [[ "$got" == "$want" ]] || fail "vendor:class set '$got'"
}

test_write_eco_udev_never_matches_intel() {
    helper write eco asus-wmi
    ! grep -q '0x8086' "$SB$UDEV" || fail "udev rule mentions Intel"
}

test_write_eco_udev_syntax() {
    helper write eco asus-wmi
    if ! udevadm verify --help > /dev/null 2>&1; then
        echo "  (udevadm verify not available, skipped)"
        return 0
    fi
    udevadm verify --no-style "$SB$UDEV" > "$SB/out" 2>&1 || fail "udevadm verify failed"
}

# -- persist / unpersist --------------------------------------------------------
test_persist_eco() {
    helper persist eco
    expect_rc 0 || return 1
    expect_content $PERSISTENT eco || return 1
    expect_mode $PERSISTENT 644 || return 1
}

test_persist_defaults_eco() {
    helper persist
    expect_rc 0 || return 1
    expect_content $PERSISTENT eco || return 1
}

test_persist_rejects_non_eco() {
    for mode in standard optimized ultimate ECO "../eco"; do
        new_sandbox "persist_rejects"
        helper persist "$mode"
        [[ "$RC" == 1 ]] || { fail "persist '$mode' exit $RC, expected 1"; return 1; }
        expect_missing $PERSISTENT || return 1
    done
}

test_persist_does_not_touch_blocks_or_trigger() {
    helper persist eco
    expect_missing $MODPROBE || return 1
    expect_missing $UDEV || return 1
    expect_missing $TRIGGER || return 1
}

test_unpersist_removes_marker_only() {
    helper write eco asus-wmi
    helper persist eco
    helper unpersist
    expect_rc 0 || return 1
    expect_missing $PERSISTENT || return 1
    expect_exists $MODPROBE || return 1
    expect_content $TRIGGER eco || return 1
}

test_unpersist_when_missing_ok() {
    helper unpersist
    expect_rc 0
}

# -- clean / uninstall ----------------------------------------------------------
test_clean_removes_eco_artifacts_keeps_prefs() {
    helper write eco pci
    helper persist eco
    helper clean
    expect_rc 0 || return 1
    expect_missing $MODPROBE || return 1
    expect_missing $UDEV || return 1
    expect_missing $TRIGGER || return 1
    expect_content $BACKEND pci || return 1
    expect_content $PERSISTENT eco || return 1
}

test_clean_when_empty_ok() {
    helper clean
    expect_rc 0
}

test_uninstall_removes_everything_it_owns() {
    helper write eco pci
    helper persist eco
    put /etc/ghelper/last-recovery "x"
    helper uninstall
    expect_rc 0 || return 1
    for f in $MODPROBE $UDEV $TRIGGER $PERSISTENT $BACKEND; do
        expect_missing "$f" || return 1
    done
    expect_exists /etc/ghelper/last-recovery || return 1
}

# -- set-backend ----------------------------------------------------------------
test_set_backend_pci_only_marker() {
    helper write eco asus-wmi
    helper set-backend pci
    expect_rc 0 || return 1
    expect_content $BACKEND pci || return 1
    expect_exists $MODPROBE || return 1
    expect_content $TRIGGER eco || return 1
}

test_set_backend_defaults_asus_wmi() {
    helper set-backend
    expect_rc 0 || return 1
    expect_content $BACKEND asus-wmi || return 1
    expect_mode $BACKEND 644 || return 1
}

test_set_backend_invalid_keeps_marker() {
    put $BACKEND pci
    for backend in nvidia PCI "pci;x" "../pci"; do
        helper set-backend "$backend"
        [[ "$RC" == 1 ]] || { fail "backend '$backend' exit $RC, expected 1"; return 1; }
        expect_content $BACKEND pci || return 1
    done
}

# -- live-standard --------------------------------------------------------------
test_live_standard_removes_blocks_and_markers() {
    helper write eco pci
    helper persist eco
    helper live-standard
    expect_rc 0 || return 1
    for f in $MODPROBE $UDEV $TRIGGER $PERSISTENT; do
        expect_missing "$f" || return 1
    done
    expect_content $BACKEND pci || return 1
}

test_live_standard_reloads_rescans_loads_drivers() {
    helper live-standard
    expect_rc 0 || return 1
    expect_calls $'udevadm control --reload-rules\nmodprobe nvidia\nmodprobe amdgpu' || return 1
    expect_content /sys/bus/pci/rescan 1 || return 1
}

test_live_standard_tool_failures_ignored() {
    helper write eco pci
    GH_STUB_EXIT=1 GH_ROOT="$SB" GH_STUBS="$STUBS" bash "$COPY" live-standard > "$SB/out" 2>&1
    RC=$?
    expect_rc 0 || return 1
    expect_missing $MODPROBE || return 1
}

test_live_standard_rescan_missing_ignored() {
    rm -f "$SB/sys/bus/pci/rescan"
    rmdir "$SB/sys/bus/pci"
    helper live-standard
    expect_rc 0
}

# -- usage ----------------------------------------------------------------------
test_no_args_usage() {
    helper
    expect_rc 1 || return 1
    expect_out "Usage:" || return 1
    expect_no_files || return 1
}

test_unknown_subcommand_usage() {
    for cmd in install remove WRITE "write;clean" --help; do
        helper "$cmd"
        [[ "$RC" == 1 ]] || { fail "'$cmd' exit $RC, expected 1"; return 1; }
    done
    expect_no_files
}

# -- Helper output read by the boot script --------------------------------------
boot() { GHELPER_TEST_ROOT="$SB" bash "$BOOT_SCRIPT" > "$SB/boot.log" 2>&1; }
fake_asus() {
    mkdir -p "$SB/sys/bus/platform/devices/asus-nb-wmi" "$SB/sys/bus/pci/devices" "$SB/sys/module"
    echo "$1" > "$SB/sys/bus/platform/devices/asus-nb-wmi/dgpu_disable"
    echo "$2" > "$SB/sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode"
}

test_boot_applies_helper_eco_asus_wmi() {
    fake_asus 0 1
    helper write eco asus-wmi
    boot
    expect_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 1 || return 1
    expect_missing $TRIGGER || return 1
    expect_missing $MODPROBE || return 1
    expect_missing $UDEV || return 1
}

test_boot_keeps_helper_eco_pci_blocks() {
    fake_asus 0 1
    helper write eco pci
    boot
    expect_missing $TRIGGER || return 1
    expect_exists $MODPROBE || return 1
    expect_exists $UDEV || return 1
    expect_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 0 || return 1
}

test_boot_applies_helper_standard_asus_wmi() {
    fake_asus 1 1
    helper write standard asus-wmi
    boot
    expect_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 0 || return 1
    expect_missing $TRIGGER || return 1
}

test_boot_applies_helper_standard_pci() {
    fake_asus 0 1
    helper write eco pci
    helper write standard pci
    boot
    expect_missing $TRIGGER || return 1
    expect_missing $MODPROBE || return 1
    grep -q "PCI backend - removing block artifacts" "$SB/boot.log" || fail "boot log: no PCI standard path"
}

test_boot_persistent_eco_from_helper() {
    fake_asus 0 1
    helper persist eco
    boot
    expect_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 1 || return 1
    expect_content $PERSISTENT eco || return 1
}

test_boot_mux0_discards_helper_eco() {
    fake_asus 0 0
    helper write eco asus-wmi
    boot
    expect_missing $TRIGGER || return 1
    expect_missing $MODPROBE || return 1
    expect_missing $UDEV || return 1
    expect_content /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable 0 || return 1
}

# -- Path contract: helper, boot script, app, installers ------------------------
# Value of VAR="..." in a shell script, with ${ROOT} / ${ROOT:+$ROOT} removed.
shell_var() { grep -m1 -E "^$2=" "$1" | sed -E 's/^[^=]+="?//; s/"$//; s/\$\{ROOT(:\+\$ROOT)?\}//'; }
# Value of a C# string constant: <name> = TestPathPrefix + "..."
cs_path() { grep -m1 -E "$2 = TestPathPrefix \+ \"" "$1" | sed -E 's/.*\+ "([^"]+)".*/\1/'; }

GPU_MODE_CS="$REPO_DIR/src/Gpu/GPUModeControl.cs"

test_contract_block_paths() {
    local h b c
    h=$(shell_var "$HELPER" MODPROBE_DEST); b=$(shell_var "$BOOT_SCRIPT" MODPROBE_BLOCK); c=$(cs_path "$GPU_MODE_CS" ModprobeBlockPath)
    [[ -n "$h" && "$h" == "$b" && "$h" == "$c" ]] || { fail "modprobe block: helper=$h boot=$b app=$c"; return 1; }
    h=$(shell_var "$HELPER" UDEV_DEST); b=$(shell_var "$BOOT_SCRIPT" UDEV_BLOCK); c=$(cs_path "$GPU_MODE_CS" UdevRemovePath)
    [[ -n "$h" && "$h" == "$b" && "$h" == "$c" ]] || fail "udev rule: helper=$h boot=$b app=$c"
}

test_contract_marker_paths() {
    local dir
    dir=$(shell_var "$HELPER" TRIGGER_DIR)
    [[ "$dir" == /etc/ghelper ]] || { fail "helper TRIGGER_DIR=$dir"; return 1; }
    local pair name helper_var boot_var cs_name h b c
    for pair in "TRIGGER_DEST:TRIGGER:TriggerPath" "PERSISTENT_DEST:PERSISTENT_TRIGGER:PersistentTriggerPath" "BACKEND_DEST:BACKEND_FILE:BackendPath"; do
        IFS=: read -r helper_var boot_var cs_name <<<"$pair"
        h=$(shell_var "$HELPER" "$helper_var"); h="${h/\$TRIGGER_DIR/$dir}"
        b=$(shell_var "$BOOT_SCRIPT" "$boot_var")
        c=$(cs_path "$GPU_MODE_CS" "$cs_name")
        [[ -n "$h" && "$h" == "$b" && "$h" == "$c" ]] || { fail "$cs_name: helper=$h boot=$b app=$c"; return 1; }
    done
}

test_contract_modprobe_modules_match() {
    helper write eco asus-wmi
    local h b c
    h=$(grep -oE '^install [a-z_]+ /bin/false' "$SB$MODPROBE" | sort | tr '\n' ' ')
    b=$(sed -n '/^install_block_artifacts()/,/^}/p' "$BOOT_SCRIPT" | grep -oE '^install [a-z_]+ /bin/false' | sort | tr '\n' ' ')
    c=$(grep -oE '"install [a-z_]+ /bin/false' "$GPU_MODE_CS" | tr -d '"' | sort | tr '\n' ' ')
    [[ -n "$h" && "$h" == "$b" && "$h" == "$c" ]] || fail "blocked modules: helper=[$h] boot=[$b] app=[$c]"
}

test_contract_boot_udev_display_rules_keep_boot_vga() {
    local line n=0
    while IFS= read -r line; do
        n=$((n + 1))
        [[ "$line" == *'ATTR{boot_vga}!="1"'* ]] || { fail "boot script display rule can remove the boot GPU: $line"; return 1; }
    done < <(sed -n '/^install_block_artifacts()/,/^}/p' "$BOOT_SCRIPT" | grep -E '^ACTION==.*ATTR\{class\}=="0x030[02]00"')
    [[ "$n" -gt 0 ]] || fail "no display rules found in the boot script"
}

test_contract_app_udev_vendor_class_set_matches_helper() {
    helper write eco asus-wmi
    local h c
    h=$(udev_rules | sed -E 's/.*ATTR\{vendor\}=="([^"]+)".*ATTR\{class\}=="([^"]+)".*/\1:\2/' | sort | tr '\n' ' ')
    c=$(grep -oE 'ATTR\{vendor\}==\\"0x[0-9a-f]+\\", ATTR\{class\}==\\"0x[0-9a-f]+\\"' "$GPU_MODE_CS" \
        | sed -E 's/.*vendor\}==\\"([^\\]+)\\".*class\}==\\"([^\\]+)\\"/\1:\2/' | sort | tr '\n' ' ')
    [[ -n "$h" && "$h" == "$c" ]] || fail "udev vendor:class helper=[$h] app=[$c]"
}

test_contract_app_finds_installed_helper() {
    local first
    first=$(grep -A3 'HelperSearchPaths = new' "$GPU_MODE_CS" | grep -m1 -oE '"/[^"]+"' | tr -d '"')
    [[ "$first" == /usr/local/lib/ghelper/gpu-block-helper.sh ]] || { fail "app looks first at $first"; return 1; }
    grep -q "HELPER_DIR=\"/usr/local/lib/ghelper\"" "$INSTALL_DIR/install.sh" || { fail "install.sh HELPER_DIR moved"; return 1; }
    grep -q 'HELPER_DEST="$HELPER_DIR/gpu-block-helper.sh"' "$INSTALL_DIR/install.sh" || { fail "install.sh helper dest moved"; return 1; }
    grep -q 'Dest = "/usr/local/lib/ghelper/gpu-block-helper.sh"' "$REPO_DIR/src/Install/Installer.cs" || fail "Repair installs the helper elsewhere"
}

test_contract_gpu_helper_path() {
    local boot app
    boot=$(shell_var "$BOOT_SCRIPT" GPU_HELPER)
    [[ "$boot" == /usr/local/lib/ghelper/gpu-helper ]] || { fail "boot script gpu-helper=$boot"; return 1; }
    app=$(grep -m1 -oE 'foreach \(var p in new\[\] \{ "[^"]+"' "$REPO_DIR/src/Platform/Linux/SysfsHelper.cs" | grep -oE '"[^"]+"$' | tr -d '"')
    [[ "$app" == "$boot" ]] || { fail "app looks first at $app, boot script uses $boot"; return 1; }
    grep -q 'GPU_HELPER_DEST="$HELPER_DIR/gpu-helper"' "$INSTALL_DIR/install.sh" || fail "install.sh installs gpu-helper elsewhere"
}

test_contract_boot_service_execstart() {
    grep -qx 'ExecStart=/usr/local/lib/ghelper/ghelper-gpu-boot.sh' "$INSTALL_DIR/ghelper-gpu-boot.service" || { fail "service ExecStart moved"; return 1; }
    grep -q '"$HELPER_DIR/ghelper-gpu-boot.sh"' "$INSTALL_DIR/install.sh" || { fail "install.sh boot script dest moved"; return 1; }
    grep -q 'Dest = "/usr/local/lib/ghelper/ghelper-gpu-boot.sh"' "$REPO_DIR/src/Install/Installer.cs" || fail "Repair installs the boot script elsewhere"
}

test_contract_boot_service_can_write_artifacts() {
    local svc="$INSTALL_DIR/ghelper-gpu-boot.service" p
    for p in "-/etc/ghelper" /etc/modprobe.d /etc/udev/rules.d; do
        grep -qx "ReadWritePaths=$p" "$svc" || { fail "service cannot write $p"; return 1; }
    done
    grep -qx 'Type=oneshot' "$svc" || { fail "service is not oneshot"; return 1; }
    grep -qE '^Before=.*display-manager\.service' "$svc" || fail "service no longer runs before the display manager"
}

test_contract_sudoers_grant_both_helpers() {
    local f
    for f in "$INSTALL_DIR/install.sh" "$INSTALL_DIR/install-local.sh"; do
        grep -q 'NOPASSWD: $HELPER_DEST' "$f" || { fail "$(basename "$f"): no sudoers line for the block helper"; return 1; }
        grep -q 'NOPASSWD: $HELPER_DIR/gpu-helper' "$f" || { fail "$(basename "$f"): no sudoers line for gpu-helper"; return 1; }
    done
    grep -q 'NOPASSWD: /usr/local/lib/ghelper/gpu-block-helper.sh' "$REPO_DIR/src/Install/Installer.cs" || fail "Repair sudoers has no block helper line"
}

test_contract_helper_syntax() {
    bash -n "$HELPER" > "$SB/out" 2>&1 || { fail "bash -n gpu-block-helper.sh"; return 1; }
    bash -n "$BOOT_SCRIPT" > "$SB/out" 2>&1 || fail "bash -n ghelper-gpu-boot.sh"
}

# -- Run ------------------------------------------------------------------------
echo "="
echo " gpu-block-helper.sh tests"
echo "="

for name in \
    write_eco_asus_wmi \
    write_eco_pci \
    write_defaults_eco_asus_wmi \
    write_creates_etc_ghelper \
    write_file_modes \
    write_standard_removes_blocks \
    write_optimized_no_blocks \
    write_ultimate_no_blocks \
    write_overwrites_trigger_and_backend \
    write_keeps_persistent_marker \
    write_invalid_mode_rejected \
    write_invalid_backend_rejected \
    write_eco_modprobe_blocks_all_dgpu_modules \
    write_eco_udev_rules_shape \
    write_eco_udev_display_rules_keep_boot_vga \
    write_eco_udev_class_set \
    write_eco_udev_never_matches_intel \
    write_eco_udev_syntax \
    persist_eco \
    persist_defaults_eco \
    persist_rejects_non_eco \
    persist_does_not_touch_blocks_or_trigger \
    unpersist_removes_marker_only \
    unpersist_when_missing_ok \
    clean_removes_eco_artifacts_keeps_prefs \
    clean_when_empty_ok \
    uninstall_removes_everything_it_owns \
    set_backend_pci_only_marker \
    set_backend_defaults_asus_wmi \
    set_backend_invalid_keeps_marker \
    live_standard_removes_blocks_and_markers \
    live_standard_reloads_rescans_loads_drivers \
    live_standard_tool_failures_ignored \
    live_standard_rescan_missing_ignored \
    no_args_usage \
    unknown_subcommand_usage \
    boot_applies_helper_eco_asus_wmi \
    boot_keeps_helper_eco_pci_blocks \
    boot_applies_helper_standard_asus_wmi \
    boot_applies_helper_standard_pci \
    boot_persistent_eco_from_helper \
    boot_mux0_discards_helper_eco \
    contract_block_paths \
    contract_marker_paths \
    contract_modprobe_modules_match \
    contract_boot_udev_display_rules_keep_boot_vga \
    contract_app_udev_vendor_class_set_matches_helper \
    contract_app_finds_installed_helper \
    contract_gpu_helper_path \
    contract_boot_service_execstart \
    contract_boot_service_can_write_artifacts \
    contract_sudoers_grant_both_helpers \
    contract_helper_syntax \
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
