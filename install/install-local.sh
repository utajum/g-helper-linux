#!/usr/bin/env bash
set -euo pipefail

# ╔══════════════════════════════════════════════════════════════════════════════╗
# ║  G-HELPER LINUX — LOCAL DEPLOYMENT SEQUENCE                                 ║
# ║  Installs from local build (dist/) produced by build.sh                     ║
# ║  100% idempotent — safe to re-run infinitely                                ║
# ║                                                                              ║
# ║  ./build.sh && sudo ./install/install-local.sh                               ║
# ╚══════════════════════════════════════════════════════════════════════════════╝

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
DIST_DIR="$PROJECT_DIR/dist"

# ── ANSI color matrix ──────────────────────────────────────────────────────────
if [[ -t 1 ]] || [[ "${FORCE_COLOR:-}" == "1" ]]; then
    RED=$'\033[0;91m'
    GREEN=$'\033[0;92m'
    YELLOW=$'\033[0;93m'
    BLUE=$'\033[0;94m'
    MAGENTA=$'\033[0;95m'
    CYAN=$'\033[0;96m'
    DIM=$'\033[2m'
    BOLD=$'\033[1m'
    RESET=$'\033[0m'
else
    RED="" GREEN="" YELLOW="" BLUE="" MAGENTA="" CYAN="" DIM="" BOLD="" RESET=""
fi

# ── Configuration ──────────────────────────────────────────────────────────────
BINARY_DEST="/usr/local/bin/ghelper"
UDEV_DEST="/etc/udev/rules.d/90-ghelper.rules"
DESKTOP_DEST="/usr/share/applications/ghelper.desktop"

# ── Counters ───────────────────────────────────────────────────────────────────
INJECTED=0
SKIPPED=0
UPDATED=0
CHMOD_APPLIED=0
CHMOD_SKIPPED=0

# ── Status display functions ───────────────────────────────────────────────────
_inject()  { echo "  ${GREEN}[INJECT]${RESET}  $1"; ((INJECTED++)) || true; }
_update()  { echo "  ${CYAN}[UPDATE]${RESET}  $1"; ((UPDATED++)) || true; }
_skip()    { echo "  ${DIM}[SKIP]${RESET}    ${DIM}$1${RESET}"; ((SKIPPED++)) || true; }
_chmod()   { echo "  ${MAGENTA}[CHMOD]${RESET}  $1"; ((CHMOD_APPLIED++)) || true; }
_chok()    { echo "  ${DIM}[OK]${RESET}      ${DIM}$1${RESET}"; ((CHMOD_SKIPPED++)) || true; }
_fail()    { echo "  ${RED}[FAIL]${RESET}    $1"; }
_info()    { echo "  ${BLUE}[INFO]${RESET}    $1"; }
_warn()    { echo "  ${YELLOW}[WARN]${RESET}    $1"; }

# ── Typing effect ──────────────────────────────────────────────────────────────
_typeout() {
    local text="$1" delay="${2:-0.02}"
    for ((i=0; i<${#text}; i++)); do
        printf "%s" "${text:$i:1}"
        sleep "$delay"
    done
    echo ""
}

# ── Hex step header ───────────────────────────────────────────────────────────
_step() {
    local hex=$1 title="$2"
    echo ""
    echo "${MAGENTA}  ┌──────────────────────────────────────────────────────┐${RESET}"
    echo "${MAGENTA}  │${RESET} ${BOLD}${CYAN}[0x$(printf '%02X' "$hex")]${RESET} ${BOLD}$title${RESET}"
    echo "${MAGENTA}  └──────────────────────────────────────────────────────┘${RESET}"
}

# ── Idempotent file install ────────────────────────────────────────────────────
# Returns 0 if skipped (unchanged), 1 if changed/new.
_install_file() {
    local src="$1" dest="$2" mode="$3" label="$4"
    if [[ -f "$dest" ]]; then
        if cmp -s "$src" "$dest"; then
            _skip "$label → already deployed at $dest"
            return 0
        else
            install -m "$mode" "$src" "$dest"
            _update "$label → $dest"
            return 1
        fi
    else
        install -m "$mode" "$src" "$dest"
        _inject "$label → $dest"
        return 1
    fi
}

# ── Idempotent chmod ───────────────────────────────────────────────────────────
_ensure_chmod() {
    local file="$1"
    [[ -f "$file" ]] || return 0
    local current
    current=$(stat -c '%a' "$file" 2>/dev/null || echo "000")
    if [[ "$current" == "666" ]]; then
        _chok "$file"
    else
        chmod 0666 "$file" 2>/dev/null && _chmod "$file" || true
    fi
}

# ══════════════════════════════════════════════════════════════════════════════
#  BOOT SEQUENCE
# ══════════════════════════════════════════════════════════════════════════════

clear 2>/dev/null || true
echo ""
echo "${CYAN}${BOLD}"
cat << 'BANNER'
     ██████╗       ██╗  ██╗███████╗██╗     ██████╗ ███████╗██████╗
    ██╔════╝       ██║  ██║██╔════╝██║     ██╔══██╗██╔════╝██╔══██╗
    ██║  ███╗█████╗███████║█████╗  ██║     ██████╔╝█████╗  ██████╔╝
    ██║   ██║╚════╝██╔══██║██╔══╝  ██║     ██╔═══╝ ██╔══╝  ██╔══██╗
    ╚██████╔╝      ██║  ██║███████╗███████╗██║     ███████╗██║  ╚██╗
     ╚═════╝       ╚═╝  ╚═╝╚══════╝╚══════╝╚═╝     ╚══════╝╚═╝   ╚═╝
BANNER
echo "${RESET}"
echo "${DIM}    ░▒▓█████████████████████████████████████████████████████▓▒░${RESET}"
echo ""
echo "${MAGENTA}${BOLD}    ╔══════════════════════════════════════════════════════╗${RESET}"
echo "${MAGENTA}${BOLD}    ║${RESET}  ${BOLD}LOCAL DEPLOYMENT SEQUENCE${RESET}              ${DIM}rev 1.0${RESET}       ${MAGENTA}${BOLD}║${RESET}"
echo "${MAGENTA}${BOLD}    ║${RESET}  ${DIM}PROTOCOL: VERIFY → INJECT → ARM → ACTIVATE${RESET}        ${MAGENTA}${BOLD}║${RESET}"
echo "${MAGENTA}${BOLD}    ╚══════════════════════════════════════════════════════╝${RESET}"
echo ""
sleep 0.3

# ── Root check ─────────────────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
    echo ""
    echo "${RED}${BOLD}  ╔══[ ACCESS DENIED ]═══════════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${RED}INSUFFICIENT PRIVILEGES :: EUID=$EUID${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}This payload requires root access.${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${YELLOW}Re-run with:${RESET} sudo $0"
    echo "${RED}${BOLD}  ╚═════════════════════════════════════════════════════╝${RESET}"
    exit 1
fi

REAL_USER=$(logname 2>/dev/null || echo "${SUDO_USER:-}")

echo "${GREEN}  ▸ ROOT ACCESS${RESET} ${DIM}........................${RESET} ${GREEN}CONFIRMED${RESET}"
echo "${GREEN}  ▸ USER${RESET} ${DIM}..............................${RESET} ${CYAN}${REAL_USER:-unknown}${RESET}"
echo "${GREEN}  ▸ SOURCE${RESET} ${DIM}............................${RESET} ${CYAN}$DIST_DIR/${RESET}"

# ══════════════════════════════════════════════════════════════════════════════
#  [0x01] VERIFY LOCAL BUILD
# ══════════════════════════════════════════════════════════════════════════════

_step 1 "SCANNING LOCAL BUILD ARTIFACTS"

if [[ ! -f "$DIST_DIR/ghelper" ]]; then
    echo ""
    echo "${RED}${BOLD}  ╔══[ BUILD NOT FOUND ]═════════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${RED}No binary at:${RESET} $DIST_DIR/ghelper"
    echo "${RED}${BOLD}  ║${RESET}  ${YELLOW}Run ./build.sh first${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}...or use install.sh to download latest release${RESET}"
    echo "${RED}${BOLD}  ╚═════════════════════════════════════════════════════╝${RESET}"
    exit 1
fi

BINARY_SIZE=$(du -sh "$DIST_DIR/ghelper" | cut -f1)
_info "Binary located: ${BOLD}$DIST_DIR/ghelper${RESET} ${DIM}(${BINARY_SIZE})${RESET}"

# Count dist files
DIST_FILES=("$DIST_DIR"/*)
_info "Artifacts found: ${GREEN}${#DIST_FILES[@]} files${RESET}"

# ══════════════════════════════════════════════════════════════════════════════
#  [0x02] INJECT BINARY
# ══════════════════════════════════════════════════════════════════════════════

_step 2 "INJECTING BINARY INTO PATH"

_install_file "$DIST_DIR/ghelper" "$BINARY_DEST" 755 "ghelper binary" || true

# ══════════════════════════════════════════════════════════════════════════════
#  [0x03] DEPLOY UDEV RULESET
# ══════════════════════════════════════════════════════════════════════════════

_step 3 "DEPLOYING UDEV RULESET"

# Always write + reload + trigger udev rules unconditionally.
# The rules list may grow between releases, and the daemon may not have
# loaded them even if the file on disk looks the same.
install -m 644 "$SCRIPT_DIR/90-ghelper.rules" "$UDEV_DEST"
_inject "udev rules → $UDEV_DEST"

udevadm control --reload-rules
_info "udev daemon reloaded"

udevadm trigger
_info "udev trigger fired — re-applying all RUN commands"

# ══════════════════════════════════════════════════════════════════════════════
#  [0x04] ESTABLISH SYSFS ACCESS LAYER
# ══════════════════════════════════════════════════════════════════════════════

_step 4 "ESTABLISHING SYSFS ACCESS LAYER"

echo "  ${DIM}(permissions reset on reboot — always re-applied)${RESET}"

# Fixed-path sysfs nodes
for f in \
    /sys/devices/platform/asus-nb-wmi/throttle_thermal_policy \
    /sys/devices/platform/asus-nb-wmi/panel_od \
    /sys/devices/platform/asus-nb-wmi/ppt_pl1_spl \
    /sys/devices/platform/asus-nb-wmi/ppt_pl2_sppt \
    /sys/devices/platform/asus-nb-wmi/ppt_fppt \
    /sys/devices/platform/asus-nb-wmi/nv_dynamic_boost \
    /sys/devices/platform/asus-nb-wmi/nv_temp_target \
    /sys/bus/platform/devices/asus-nb-wmi/dgpu_disable \
    /sys/bus/platform/devices/asus-nb-wmi/gpu_mux_mode \
    /sys/bus/platform/devices/asus-nb-wmi/mini_led_mode \
    /sys/module/pcie_aspm/parameters/policy \
    /sys/firmware/acpi/platform_profile \
    /sys/devices/system/cpu/intel_pstate/no_turbo \
    /sys/devices/system/cpu/cpufreq/boost \
    /sys/class/leds/asus::kbd_backlight/brightness \
    /sys/class/leds/asus::kbd_backlight/multi_intensity; do
    _ensure_chmod "$f"
done

# ── Battery charge limit ──
_bat_count=0
for f in /sys/class/power_supply/BAT*/charge_control_end_threshold; do
    [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_bat_count++)) || true; }
done
[[ $_bat_count -eq 0 ]] && _info "${DIM}no battery charge_control_end_threshold found${RESET}"

# ── Backlight ──
_bl_count=0
for f in /sys/class/backlight/*/brightness; do
    [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_bl_count++)) || true; }
done
[[ $_bl_count -eq 0 ]] && _info "${DIM}no backlight brightness nodes found${RESET}"

# ── CPU online/offline ──
_cpu_count=0
for f in /sys/devices/system/cpu/cpu*/online; do
    [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_cpu_count++)) || true; }
done
if [[ $_cpu_count -gt 0 ]]; then
    _info "CPU core online/offline: ${GREEN}${_cpu_count} nodes${RESET} processed"
else
    _info "${DIM}no CPU online/offline nodes found${RESET}"
fi

# ── Fan curves (hwmon) ──
_hwmon_found=0
for hwmon in /sys/class/hwmon/hwmon*; do
    name=$(cat "$hwmon/name" 2>/dev/null || echo "")
    if [[ "$name" == "asus_nb_wmi" || "$name" == "asus_custom_fan_curve" ]]; then
        _fan_count=0
        for f in "$hwmon"/pwm*_auto_point* "$hwmon"/pwm*_enable; do
            [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_fan_count++)) || true; }
        done
        _info "${CYAN}$(basename "$hwmon")${RESET} (${BOLD}$name${RESET}) — ${GREEN}${_fan_count} fan curve nodes${RESET}"
        ((_hwmon_found++)) || true
    fi
done
[[ $_hwmon_found -eq 0 ]] && _info "${DIM}no asus fan curve hwmon devices found${RESET}"

echo ""
_info "sysfs summary: ${GREEN}${CHMOD_APPLIED} armed${RESET} / ${DIM}${CHMOD_SKIPPED} already 0666${RESET}"

# ══════════════════════════════════════════════════════════════════════════════
#  [0x05] DESKTOP INTEGRATION
# ══════════════════════════════════════════════════════════════════════════════

_step 5 "DESKTOP INTEGRATION LAYER"

# Always overwrite — .desktop entry may have new categories, keywords, or exec path
install -m 644 "$SCRIPT_DIR/ghelper.desktop" "$DESKTOP_DEST"
_inject "desktop entry → $DESKTOP_DEST"

# Icon
ICON_SRC="$PROJECT_DIR/src/UI/Assets/favicon.ico"
ICON_DEST="/usr/share/icons/hicolor/64x64/apps"
if [[ -f "$ICON_SRC" ]]; then
    mkdir -p "$ICON_DEST"
    if command -v convert &>/dev/null; then
        # ImageMagick available — convert ICO → PNG
        if [[ -f "$ICON_DEST/ghelper.png" ]]; then
            # Generate temp conversion and compare
            ICON_TMP=$(mktemp /tmp/ghelper-icon-XXXXXX.png)
            convert "$ICON_SRC[0]" "$ICON_TMP" 2>/dev/null
            if cmp -s "$ICON_TMP" "$ICON_DEST/ghelper.png"; then
                _skip "icon → already deployed at $ICON_DEST/ghelper.png"
            else
                mv "$ICON_TMP" "$ICON_DEST/ghelper.png"
                _update "icon → $ICON_DEST/ghelper.png"
            fi
            rm -f "$ICON_TMP" 2>/dev/null || true
        else
            convert "$ICON_SRC[0]" "$ICON_DEST/ghelper.png" 2>/dev/null
            _inject "icon → $ICON_DEST/ghelper.png"
        fi
    else
        # No ImageMagick — copy ICO directly
        if [[ -f "$ICON_DEST/ghelper.ico" ]] && cmp -s "$ICON_SRC" "$ICON_DEST/ghelper.ico"; then
            _skip "icon → already deployed at $ICON_DEST/ghelper.ico"
        else
            cp "$ICON_SRC" "$ICON_DEST/ghelper.ico"
            # Patch desktop entry to use absolute path
            sed -i 's|Icon=ghelper|Icon=/usr/share/icons/hicolor/64x64/apps/ghelper.ico|' \
                "$DESKTOP_DEST" 2>/dev/null || true
            _inject "icon → $ICON_DEST/ghelper.ico ${DIM}(no ImageMagick — raw ICO)${RESET}"
        fi
    fi
    gtk-update-icon-cache /usr/share/icons/hicolor 2>/dev/null || true
else
    _warn "No icon source found at $ICON_SRC"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x06] AUTOSTART IMPLANT
# ══════════════════════════════════════════════════════════════════════════════

_step 6 "AUTOSTART IMPLANT"

if [[ -n "$REAL_USER" ]]; then
    AUTOSTART_DIR="/home/$REAL_USER/.config/autostart"
    AUTOSTART_DEST="$AUTOSTART_DIR/ghelper.desktop"
    mkdir -p "$AUTOSTART_DIR"
    install -m 644 "$SCRIPT_DIR/ghelper.desktop" "$AUTOSTART_DEST"
    chown "$REAL_USER:$REAL_USER" "$AUTOSTART_DEST"
    _inject "autostart for user ${BOLD}$REAL_USER${RESET} → $AUTOSTART_DEST"
else
    _warn "Could not determine real user — skipping autostart"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  DEPLOYMENT COMPLETE
# ══════════════════════════════════════════════════════════════════════════════

echo ""
echo ""
echo "${GREEN}${BOLD}  ╔══════════════════════════════════════════════════════════╗${RESET}"
echo "${GREEN}${BOLD}  ║${RESET}                                                          ${GREEN}${BOLD}║${RESET}"
echo "${GREEN}${BOLD}  ║${RESET}  ${BOLD}${GREEN}▓▓▓ DEPLOYMENT SEQUENCE COMPLETE ▓▓▓${RESET}                 ${GREEN}${BOLD}║${RESET}"
echo "${GREEN}${BOLD}  ║${RESET}                                                          ${GREEN}${BOLD}║${RESET}"
echo "${GREEN}${BOLD}  ╠══════════════════════════════════════════════════════════╣${RESET}"
echo "${GREEN}${BOLD}  ║${RESET}                                                          ${GREEN}${BOLD}║${RESET}"
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF0${RESET}  Binary    ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "$BINARY_DEST"
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF1${RESET}  udev      ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "$UDEV_DEST"
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF2${RESET}  Desktop   ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "$DESKTOP_DEST"
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF3${RESET}  Autostart ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "~/.config/autostart/ghelper.desktop"
echo "${GREEN}${BOLD}  ║${RESET}                                                          ${GREEN}${BOLD}║${RESET}"
echo "${GREEN}${BOLD}  ╠══════════════════════════════════════════════════════════╣${RESET}"
echo "${GREEN}${BOLD}  ║${RESET}                                                          ${GREEN}${BOLD}║${RESET}"
printf "${GREEN}${BOLD}  ║${RESET}  ${GREEN}INJECTED: %-3d${RESET} ${CYAN}UPDATED: %-3d${RESET} ${DIM}SKIPPED: %-3d${RESET}             ${GREEN}${BOLD}║${RESET}\n" "$INJECTED" "$UPDATED" "$SKIPPED"
printf "${GREEN}${BOLD}  ║${RESET}  ${MAGENTA}CHMOD: %-3d armed${RESET}  ${DIM}%-3d already set${RESET}                  ${GREEN}${BOLD}║${RESET}\n" "$CHMOD_APPLIED" "$CHMOD_SKIPPED"
echo "${GREEN}${BOLD}  ║${RESET}                                                          ${GREEN}${BOLD}║${RESET}"
echo "${GREEN}${BOLD}  ╚══════════════════════════════════════════════════════════╝${RESET}"
echo ""

_typeout "${GREEN}${BOLD}  > NEURAL LINK ESTABLISHED :: LAUNCH WITH: ghelper${RESET}" 0.03

echo ""
