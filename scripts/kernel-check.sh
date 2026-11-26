#!/usr/bin/env bash
# kernel-check.sh - Track Linux kernel ASUS/Lenovo platform drivers and hid-asus for
# features portable to g-helper-linux. Kernel analog of upstream-check.sh.
#
# Usage:
#   ./scripts/kernel-check.sh             # commits touching watched drivers since last check
#   ./scripts/kernel-check.sh --details   # + per-commit diffs of watched files
#   ./scripts/kernel-check.sh --since REF # scan REF..HEAD (rename-aware; spans the v7.1 lenovo/ move)
#   ./scripts/kernel-check.sh --catalog   # full present-day attribute/device-id inventory + port cross-ref
#   ./scripts/kernel-check.sh --mark      # save current kernel HEAD as "last checked"
#   ./scripts/kernel-check.sh --reset     # set baseline to current HEAD without review
#
# Requires: a torvalds/linux clone at ../linux (relative to repo root).
# Historical floor for --since: v6.16 (start of the asus-armoury / lenovo-wmi era).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
KERNEL_DIR="$(cd "$REPO_ROOT/../linux" 2>/dev/null && pwd)" || { echo "ERROR: kernel clone not found at ../linux (clone torvalds/linux there)"; exit 1; }
STATE_FILE="$REPO_ROOT/.kernel-last-checked"
SRC_DIR="$REPO_ROOT/src"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; DIM='\033[2m'; BOLD='\033[1m'; NC='\033[0m'

# Watched driver files. Kernel 7.5 moves the ASUS drivers from x86/ to asus/;
# both layouts are listed so every range spans the move.
ASUS_PATHS=(
    drivers/platform/asus/asus-armoury.c
    drivers/platform/asus/asus-armoury.h
    drivers/platform/asus/asus-nb-wmi.c
    drivers/platform/asus/asus-wmi.c
    drivers/platform/asus/asus-wmi.h
    drivers/platform/x86/asus-armoury.c
    drivers/platform/x86/asus-armoury.h
    drivers/platform/x86/asus-nb-wmi.c
    drivers/platform/x86/asus-wmi.c
    drivers/platform/x86/asus-wmi.h
    include/linux/platform_data/x86/asus-wmi.h
    drivers/hid/hid-asus.c
)
LENOVO_PATHS=(
    drivers/platform/x86/lenovo/ideapad-laptop.c
    drivers/platform/x86/lenovo/ideapad-laptop.h
    drivers/platform/x86/lenovo/wmi-gamezone.c
    drivers/platform/x86/lenovo/wmi-other.c
    drivers/platform/x86/lenovo/wmi-events.c
    drivers/platform/x86/lenovo/wmi-events.h
    drivers/platform/x86/lenovo/wmi-capdata.c
    drivers/platform/x86/lenovo/wmi-capdata.h
    drivers/platform/x86/lenovo/wmi-helpers.c
    drivers/platform/x86/lenovo/wmi-helpers.h
    drivers/platform/x86/lenovo/wmi-camera.c
    drivers/platform/x86/lenovo/wmi-hotkey-utilities.c
    drivers/platform/x86/lenovo/ymc.c
    drivers/platform/x86/lenovo/think-lmi.c
    drivers/platform/x86/lenovo/think-lmi.h
    drivers/platform/x86/lenovo/thinkpad_acpi.c
)
# Pre-v7.1 locations (the lenovo/ subdir reorg). Added so --since spans the move.
LENOVO_OLD_PATHS=(
    drivers/platform/x86/ideapad-laptop.c
    drivers/platform/x86/lenovo-wmi-gamezone.c
    drivers/platform/x86/lenovo-wmi-other.c
    drivers/platform/x86/lenovo-wmi-events.c
    drivers/platform/x86/lenovo-wmi-capdata01.c
    drivers/platform/x86/lenovo-wmi-helpers.c
    drivers/platform/x86/lenovo-wmi-camera.c
    drivers/platform/x86/lenovo-wmi-hotkey-utilities.c
    drivers/platform/x86/lenovo-ymc.c
    drivers/platform/x86/think-lmi.c
    drivers/platform/x86/thinkpad_acpi.c
)

show_details=false; mark_checked=false; reset_mode=false; catalog_mode=false; since_ref=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --details) show_details=true ;;
        --catalog) catalog_mode=true ;;
        --mark)    mark_checked=true ;;
        --reset)   reset_mode=true ;;
        --since)   since_ref="${2:-}"; shift ;;
        --since=*) since_ref="${1#--since=}" ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        *) echo "Unknown flag: $1 (see --help)"; exit 2 ;;
    esac
    shift
done

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

echo -e "${BOLD}=== Kernel Driver Check (ASUS / Lenovo platform, hid-asus) ===${NC}"
echo -e "${DIM}Fetching kernel...${NC}"
git -C "$KERNEL_DIR" fetch origin --quiet 2>/dev/null || echo -e "${YELLOW}(fetch failed - using local refs)${NC}"

# Tip = origin/master if present, else local HEAD.
TIP="$(git -C "$KERNEL_DIR" rev-parse origin/master 2>/dev/null || git -C "$KERNEL_DIR" rev-parse HEAD)"
tip_short="$(git -C "$KERNEL_DIR" rev-parse --short "$TIP")"
tip_ver="$(git -C "$KERNEL_DIR" describe --tags "$TIP" 2>/dev/null | sed -E 's/-[0-9]+-g[0-9a-f]+$//')"
echo -e "Kernel HEAD: ${CYAN}${tip_short}${NC} (${tip_ver:-?})"

# --mark / --reset: save baseline and exit.
if $mark_checked || $reset_mode; then
    echo "$TIP" > "$STATE_FILE"
    echo -e "${GREEN}Marked ${tip_short} (${tip_ver:-?}) as last checked${NC}"
    exit 0
fi

# Emit the contents of each path at a ref (skips paths absent at that ref).
cat_paths() {
    local ref="$1"; shift
    local p
    for p in "$@"; do
        git -C "$KERNEL_DIR" show "${ref}:${p}" 2>/dev/null || true
    done
}

# Net added lines for a path set across BASE..TIP (rename-aware).
diff_added() {
    git -C "$KERNEL_DIR" diff -M "$1" "$TIP" -- "${@:2}" 2>/dev/null \
        | grep '^+' | grep -v '^+++' | sed 's/^+//' || true
}

# Pull identifiers of interest from stdin source lines into $TMP/* sets.
# Every pipeline ends with `|| true` so a no-match grep can't trip pipefail.
extract_signals() {
    local input; input="$(cat)"
    : > "$TMP/asus_devid"; : > "$TMP/asus_attr"; : > "$TMP/asus_attr_var"
    : > "$TMP/len_tun"; : > "$TMP/len_feat"; : > "$TMP/len_gz"; : > "$TMP/idea_attr"; : > "$TMP/pp"
    grep -oE 'ASUS_WMI_DEVID_[A-Z0-9_]+'                       <<<"$input" | sort -u > "$TMP/asus_devid" || true
    # asus-armoury: ASUS_ATTR_GROUP_*(var, "sysfs_name", ...) -> the quoted sysfs name
    grep -oE 'ASUS_ATTR_GROUP_[A-Z_]+\([a-z0-9_]+,[[:space:]]*"[a-z0-9_]+"' <<<"$input" \
        | grep -oE '"[a-z0-9_]+"' | tr -d '"' | sort -u > "$TMP/asus_attr" || true
    # ROG tunables name the attr via a macro -> capture the first (var) arg, drop macro-internal _names
    grep -oE 'ASUS_ATTR_GROUP_[A-Z_]+\([a-z0-9_]+' <<<"$input" | sed -E 's/.*\(//' | grep -vE '^_' | sort -u > "$TMP/asus_attr_var" || true
    # tunable name = the variable in `static struct tunable_attr_01 <name> = {`
    grep -oE 'struct tunable_attr_01[[:space:]]+[a-z0-9_]+[[:space:]]*=' <<<"$input" \
        | sed -E 's/.*tunable_attr_01[[:space:]]+//; s/[[:space:]]*=.*//' | sort -u > "$TMP/len_tun" || true
    grep -oE 'LWMI_FEATURE_ID_[A-Z0-9_]+'                      <<<"$input" | sort -u > "$TMP/len_feat" || true
    grep -oE 'LWMI_GZ_METHOD_ID_[A-Z0-9_]+'                    <<<"$input" | sort -u > "$TMP/len_gz" || true
    grep -oE 'DEVICE_ATTR(_RW|_RO|_WO)?\([a-z0-9_]+'           <<<"$input" | sed -E 's/.*\(//' | grep -vE '^_' | sort -u > "$TMP/idea_attr" || true
    grep -oE 'PLATFORM_PROFILE_[A-Z_]+'                        <<<"$input" | grep -vE 'PLATFORM_PROFILE_MAX$' | sort -u > "$TMP/pp" || true
}

# True if a sysfs attribute name appears anywhere in the port's src/.
in_port() { grep -rqiF "$1" "$SRC_DIR" 2>/dev/null; }

# Resolve ASUS_WMI_DEVID_<NAME> to its hex value from the kernel header.
devid_value() {
    git -C "$KERNEL_DIR" show "$TIP:include/linux/platform_data/x86/asus-wmi.h" 2>/dev/null \
        | sed -nE "s/^#define[[:space:]]+$1[[:space:]]+(0x[0-9a-fA-F]+).*/\1/p" | head -1
}

# Resolve ASUS_WMI_DEVID_<NAME> to the asus-armoury sysfs attribute that fronts
# it, via the `{ &<name>_attr_group, ASUS_WMI_DEVID_<NAME> }` table.
devid_sysfs_name() {
    [[ -f "$TMP/devid_map" ]] || cat_paths "$TIP" drivers/platform/asus/asus-armoury.c drivers/platform/x86/asus-armoury.c \
        | grep -oE '\{ &[a-z0-9_]+_attr_group, ASUS_WMI_DEVID_[A-Z0-9_]+' \
        | sed -E 's/\{ &//; s/_attr_group,[[:space:]]*/ /' > "$TMP/devid_map" || true
    awk -v d="$1" '$2 == d { print $1; exit }' "$TMP/devid_map" 2>/dev/null
}

# The port reaches most of these knobs by sysfs attribute name, and the rest by
# the raw hex passed to the WMI shim - it never spells the kernel's symbolic
# name. Checking only the name reports everything as missing, so try all three.
# Still over-reports knobs the kernel fronts as /sys/class/leds entries
# (lightbar, micmute, tuf rgb): those have no firmware-attributes group.
in_port_devid() {
    in_port "$1" && return 0
    local sysfs; sysfs="$(devid_sysfs_name "$1")"
    [[ -n "$sysfs" ]] && in_port "$sysfs" && return 0
    local hex; hex="$(devid_value "$1")"
    [[ -z "$hex" ]] && return 1
    in_port "$hex" && return 0
    in_port "0x$(printf '%x' "$hex")"
}

# Print a set; xref tags each entry [in port] / [candidate].
# xref      - match the literal name in src/
# xref-devid - resolve ASUS_WMI_DEVID_* to hex first
print_set() {
    local file="$1" label="$2" xref="${3:-}"
    [[ -s "$file" ]] || return 0
    echo -e "  ${BOLD}${label}:${NC}"
    local name tag hit
    while IFS= read -r name; do
        [[ -z "$name" ]] && continue
        case "$xref" in
            xref)       if in_port "$name"; then hit=0; else hit=1; fi ;;
            xref-devid) if in_port_devid "$name"; then hit=0; else hit=1; fi ;;
            *)          echo -e "    ${DIM}${name}${NC}"; continue ;;
        esac
        if [[ $hit -eq 0 ]]; then tag="${DIM}[in port]${NC}"; else tag="${YELLOW}[NOT IN PORT -> candidate]${NC}"; fi
        echo -e "    ${name}  ${tag}"
    done < "$file"
}

report_signals() {
    echo -e "${GREEN}${BOLD}ASUS knobs${NC}"
    print_set "$TMP/asus_attr"     "asus-armoury firmware-attributes (sysfs names)" xref
    print_set "$TMP/asus_attr_var" "asus-armoury attribute groups (vars: ppt_*/nv_* etc.)" xref
    print_set "$TMP/asus_devid"    "ASUS_WMI device IDs" xref-devid
    echo ""
    echo -e "${GREEN}${BOLD}Lenovo knobs${NC}"
    print_set "$TMP/len_tun"   "wmi-other firmware-attribute tunables" xref
    print_set "$TMP/len_feat"  "wmi-other feature IDs (CPU/GPU/PSU/FAN)"
    print_set "$TMP/len_gz"    "wmi-gamezone method IDs"
    print_set "$TMP/idea_attr" "sysfs attributes (DEVICE_ATTR: ideapad/thinkpad/armoury)" xref
    print_set "$TMP/pp"        "platform_profile choices"
}

# ---- Catalog mode: full present-day inventory ----
if $catalog_mode; then
    echo -e "\n${BOLD}=== Present-day catalog @ ${tip_ver:-$tip_short} ===${NC}"
    echo -e "${DIM}([in port] = name already referenced in src/, candidate = not yet)${NC}\n"
    extract_signals < <(cat_paths "$TIP" "${ASUS_PATHS[@]}" "${LENOVO_PATHS[@]}")
    report_signals
    exit 0
fi

# ---- Range selection ----
if [[ -n "$since_ref" ]]; then
    BASE="$(git -C "$KERNEL_DIR" rev-parse "$since_ref" 2>/dev/null)" || { echo -e "${RED}Unknown ref: $since_ref${NC}"; exit 1; }
    base_label="$since_ref"
    PATHS=("${ASUS_PATHS[@]}" "${LENOVO_PATHS[@]}" "${LENOVO_OLD_PATHS[@]}")
elif [[ -f "$STATE_FILE" ]]; then
    BASE="$(tr -d '[:space:]' < "$STATE_FILE")"
    base_label="$(git -C "$KERNEL_DIR" rev-parse --short "$BASE" 2>/dev/null || echo "$BASE")"
    PATHS=("${ASUS_PATHS[@]}" "${LENOVO_PATHS[@]}")
    if ! git -C "$KERNEL_DIR" merge-base --is-ancestor "$BASE" "$TIP" 2>/dev/null; then
        echo -e "${RED}WARNING: last-checked is not an ancestor of HEAD. Use --reset.${NC}"; exit 1
    fi
else
    echo -e "${YELLOW}No baseline recorded.${NC} Showing full catalog; run --reset to set a baseline, or --since v6.16 to scan history.\n"
    extract_signals < <(cat_paths "$TIP" "${ASUS_PATHS[@]}" "${LENOVO_PATHS[@]}")
    report_signals
    exit 0
fi

echo -e "Baseline:    ${CYAN}${base_label}${NC}"
new_count="$(git -C "$KERNEL_DIR" rev-list --count "${BASE}..${TIP}" -- "${PATHS[@]}" 2>/dev/null || echo 0)"
if [[ "$new_count" -eq 0 ]]; then
    echo -e "${GREEN}No new commits touching watched drivers.${NC}"; exit 0
fi
echo -e "New commits touching watched drivers: ${BOLD}${new_count}${NC}\n"

# ---- Commit listing, grouped ----
list_commits() {
    local label="$1"; shift
    local out; out="$(git -C "$KERNEL_DIR" log --oneline --no-merges "${BASE}..${TIP}" -- "$@" 2>/dev/null || true)"
    local n; n="$(printf '%s' "$out" | grep -c . || true)"
    echo -e "${BOLD}${label} (${n}):${NC}"
    if [[ -n "$out" ]]; then printf '%s\n' "$out" | sed 's/^/  /'; else echo -e "  ${DIM}(none)${NC}"; fi
    echo ""
}
list_commits "ASUS"   "${ASUS_PATHS[@]}"
LEN_LIST=("${LENOVO_PATHS[@]}"); [[ -n "$since_ref" ]] && LEN_LIST+=("${LENOVO_OLD_PATHS[@]}")
list_commits "Lenovo" "${LEN_LIST[@]}"

# ---- New capabilities surfaced in the range ----
echo -e "${BOLD}=== New capabilities surfaced (${base_label}..${tip_short}) ===${NC}"
echo -e "${DIM}([in port] = referenced in src/, candidate = not yet)${NC}\n"
extract_signals < <(diff_added "$BASE" "${PATHS[@]}")
report_signals

# ---- Optional per-commit diffs ----
if $show_details; then
    echo -e "\n${BOLD}=== DIFFS OF WATCHED FILES (${base_label}..${tip_short}) ===${NC}\n"
    git -C "$KERNEL_DIR" log -p --no-merges "${BASE}..${TIP}" -- "${PATHS[@]}" 2>/dev/null || true
fi

echo -e "\n${YELLOW}Run with --mark to save HEAD as checked, or --details for full diffs.${NC}"
