#!/usr/bin/env bash
set -euo pipefail

# ╔══════════════════════════════════════════════════════════════════════════════╗
# ║  G-HELPER LINUX — REMOTE DEPLOYMENT SEQUENCE                               ║
# ║  Downloads latest release + installs system-wide                            ║
# ║  100% idempotent — safe to re-run infinitely                                ║
# ║                                                                              ║
# ║  curl -sL https://raw.githubusercontent.com/utajum/g-helper-linux/master/install/install.sh | sudo bash
# ╚══════════════════════════════════════════════════════════════════════════════╝

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
REPO="utajum/g-helper-linux"
INSTALL_DIR="/opt/ghelper"
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

# ── Progress bar ───────────────────────────────────────────────────────────────
_progress_bar() {
    local current=$1 total=$2 width=30 label="${3:-}"
    local pct=$((current * 100 / total))
    local filled=$((current * width / total))
    local empty=$((width - filled))
    local bar=""
    for ((i=0; i<filled; i++)); do bar+="█"; done
    for ((i=0; i<empty; i++)); do bar+="░"; done
    printf "\r  ${CYAN}[%s]${RESET} %3d%% %s " "$bar" "$pct" "$label"
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
# Compares source and dest. Skips if identical, updates if different, injects if new.
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
echo "${RED}${BOLD}"
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
echo "${CYAN}${BOLD}    ╔══════════════════════════════════════════════════════╗${RESET}"
echo "${CYAN}${BOLD}    ║${RESET}  ${BOLD}REMOTE DEPLOYMENT SEQUENCE${RESET}            ${DIM}rev 1.0${RESET}       ${CYAN}${BOLD}║${RESET}"
echo "${CYAN}${BOLD}    ║${RESET}  ${DIM}PROTOCOL: DOWNLOAD → VERIFY → INJECT → ARM${RESET}        ${CYAN}${BOLD}║${RESET}"
echo "${CYAN}${BOLD}    ╚══════════════════════════════════════════════════════╝${RESET}"
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
echo "${GREEN}  ▸ TARGET${RESET} ${DIM}............................${RESET} ${CYAN}$INSTALL_DIR/${RESET}"
echo "${GREEN}  ▸ SOURCE${RESET} ${DIM}............................${RESET} ${CYAN}github.com/$REPO${RESET}"

# ── Temp workspace ─────────────────────────────────────────────────────────────
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

# ══════════════════════════════════════════════════════════════════════════════
#  [0x01] DOWNLOAD PAYLOADS
# ══════════════════════════════════════════════════════════════════════════════

_step 1 "DOWNLOADING PAYLOADS FROM REMOTE"

BINARIES=(ghelper libSkiaSharp.so libHarfBuzzSharp.so)
ASSETS=(90-ghelper.rules ghelper.desktop)

dl_count=0
dl_total=$(( ${#BINARIES[@]} + ${#ASSETS[@]} ))

for file in "${BINARIES[@]}"; do
    ((dl_count++)) || true
    _progress_bar "$dl_count" "$dl_total" "Fetching $file..."
    if ! curl -fsSL "https://github.com/$REPO/releases/latest/download/$file" -o "$WORK_DIR/$file" 2>/dev/null; then
        echo ""
        _fail "Download failed: $file"
        _fail "Check connection → https://github.com/$REPO/releases"
        exit 1
    fi
done

for file in "${ASSETS[@]}"; do
    ((dl_count++)) || true
    _progress_bar "$dl_count" "$dl_total" "Fetching $file..."
    if ! curl -fsSL "https://raw.githubusercontent.com/$REPO/master/install/$file" -o "$WORK_DIR/$file" 2>/dev/null; then
        echo ""
        _fail "Download failed: $file"
        exit 1
    fi
done

echo ""
chmod +x "$WORK_DIR/ghelper"
_info "All payloads acquired ${GREEN}(${dl_total} files)${RESET}"

# ══════════════════════════════════════════════════════════════════════════════
#  [0x02] INJECT BINARIES
# ══════════════════════════════════════════════════════════════════════════════

_step 2 "INJECTING BINARIES INTO TARGET"

mkdir -p "$INSTALL_DIR"

_install_file "$WORK_DIR/ghelper"             "$INSTALL_DIR/ghelper"             755 "ghelper binary" || true
_install_file "$WORK_DIR/libSkiaSharp.so"     "$INSTALL_DIR/libSkiaSharp.so"     755 "libSkiaSharp.so" || true
_install_file "$WORK_DIR/libHarfBuzzSharp.so" "$INSTALL_DIR/libHarfBuzzSharp.so" 755 "libHarfBuzzSharp.so" || true

# Symlink (ln -sf is already idempotent but we report status)
if [[ "$(readlink -f /usr/local/bin/ghelper 2>/dev/null)" == "$INSTALL_DIR/ghelper" ]]; then
    _skip "symlink → /usr/local/bin/ghelper already targets $INSTALL_DIR/ghelper"
else
    ln -sf "$INSTALL_DIR/ghelper" /usr/local/bin/ghelper
    _inject "symlink → /usr/local/bin/ghelper"
fi

# Fix ownership so the real user can run ghelper without root
if [[ -n "$REAL_USER" ]]; then
    chown -R "$REAL_USER:$REAL_USER" "$INSTALL_DIR"
    _info "ownership → ${BOLD}$REAL_USER:$REAL_USER${RESET} on $INSTALL_DIR/"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x03] DEPLOY UDEV RULESET
# ══════════════════════════════════════════════════════════════════════════════

_step 3 "DEPLOYING UDEV RULESET"

# Always write + reload + trigger udev rules unconditionally.
# The rules list may grow between releases, and the daemon may not have
# loaded them even if the file on disk looks the same.
install -m 644 "$WORK_DIR/90-ghelper.rules" "$UDEV_DEST"
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
install -m 644 "$WORK_DIR/ghelper.desktop" "$DESKTOP_DEST"
_inject "desktop entry → $DESKTOP_DEST"

# ══════════════════════════════════════════════════════════════════════════════
#  [0x06] AUTOSTART IMPLANT
# ══════════════════════════════════════════════════════════════════════════════

_step 6 "AUTOSTART IMPLANT"

if [[ -n "$REAL_USER" ]]; then
    AUTOSTART_DIR="/home/$REAL_USER/.config/autostart"
    AUTOSTART_DEST="$AUTOSTART_DIR/ghelper.desktop"
    # Create dir as the real user so ownership is correct from the start
    su -c "mkdir -p '$AUTOSTART_DIR'" "$REAL_USER"
    install -m 644 "$WORK_DIR/ghelper.desktop" "$AUTOSTART_DEST"
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
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF0${RESET}  Binary    ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "$INSTALL_DIR/ghelper"
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF1${RESET}  Symlink   ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "/usr/local/bin/ghelper"
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF2${RESET}  udev      ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "$UDEV_DEST"
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF3${RESET}  Desktop   ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "$DESKTOP_DEST"
printf "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF4${RESET}  Autostart ${DIM}→${RESET} %-38s${GREEN}${BOLD}║${RESET}\n" "~/.config/autostart/ghelper.desktop"
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
