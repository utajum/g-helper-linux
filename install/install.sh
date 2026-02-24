#!/usr/bin/env bash
set -euo pipefail

# ╔══════════════════════════════════════════════════════════════════════════════╗
# ║  G-HELPER LINUX — REMOTE DEPLOYMENT SEQUENCE                               ║
# ║  Downloads latest release + installs system-wide                            ║
# ║  100% idempotent — safe to re-run infinitely                                ║
# ║                                                                              ║
# ║  Install:    curl -sL https://raw.githubusercontent.com/utajum/g-helper-linux/master/install/install.sh | sudo bash
# ║  AppImage:   curl -sL ... | sudo bash -s -- --appimage                       ║
# ║  Uninstall:  curl -sL ... | sudo bash -s -- --uninstall                      ║
# ╚══════════════════════════════════════════════════════════════════════════════╝

# ── Mode selection ─────────────────────────────────────────────────────────────
MODE="install"
case "${1:-}" in
    --uninstall) MODE="uninstall" ;;
    --appimage)  MODE="appimage" ;;
    --help|-h)
        echo "Usage: $0 [--appimage|--uninstall|--help]"
        echo ""
        echo "  (default)     Full install: download binary + udev + permissions + desktop"
        echo "  --appimage    AppImage support: udev rules + sysfs permissions only (no binary)"
        echo "  --uninstall   Remove all installed files"
        exit 0
        ;;
esac

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

if [[ -w "/usr/share/applications" ]] 2>/dev/null; then
    DESKTOP_DEST="/usr/share/applications/ghelper.desktop"
else
    DESKTOP_DEST="$HOME/.local/share/applications/ghelper.desktop"
    mkdir -p "$HOME/.local/share/applications" 2>/dev/null || true
fi

# ── Counters ───────────────────────────────────────────────────────────────────
INJECTED=0
SKIPPED=0
UPDATED=0
CHMOD_APPLIED=0
CHMOD_SKIPPED=0
REMOVED=0

# ── Status display functions ───────────────────────────────────────────────────
_inject()  { echo "  ${GREEN}[INJECT]${RESET}  $1"; ((INJECTED++)) || true; }
_update()  { echo "  ${CYAN}[UPDATE]${RESET}  $1"; ((UPDATED++)) || true; }
_skip()    { echo "  ${DIM}[SKIP]${RESET}    ${DIM}$1${RESET}"; ((SKIPPED++)) || true; }
_chmod()   { echo "  ${MAGENTA}[CHMOD]${RESET}  $1"; ((CHMOD_APPLIED++)) || true; }
_chok()    { echo "  ${DIM}[OK]${RESET}      ${DIM}$1${RESET}"; ((CHMOD_SKIPPED++)) || true; }
_fail()    { echo "  ${RED}[FAIL]${RESET}    $1"; }
_info()    { echo "  ${BLUE}[INFO]${RESET}    $1"; }
_warn()    { echo "  ${YELLOW}[WARN]${RESET}    $1"; }
_remove()  { echo "  ${RED}[REMOVE]${RESET}  $1"; ((REMOVED++)) || true; }
_gone()    { echo "  ${DIM}[GONE]${RESET}    ${DIM}$1 (not present)${RESET}"; }

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

# ── Safe remove helper ─────────────────────────────────────────────────────────
_safe_remove() {
    local path="$1" label="$2"
    if [[ -e "$path" || -L "$path" ]]; then
        rm -rf "$path"
        _remove "$label → $path"
    else
        _gone "$label"
    fi
}

# ══════════════════════════════════════════════════════════════════════════════
#  BANNER
# ══════════════════════════════════════════════════════════════════════════════

clear 2>/dev/null || true
echo ""

if [[ "$MODE" == "uninstall" ]]; then
    echo "${RED}${BOLD}"
else
    echo "${RED}${BOLD}"
fi
cat << 'BANNER'
     ██████╗       ██╗  ██╗███████╗██╗     ██████╗ ███████╗██████╗
    ██╔════╝       ██║  ██║██╔════╝██║     ██╔══██╗██╔════╝██╔══██╗
    ██║  ███╗█████╗███████║█████╗  ██║     ██████╔╝█████╗  ██████╔╝
    ██║   ██║╚════╝██╔══██║██╔══╝  ██║     ██╔═══╝ ██╔══╝  ██╔══██╗
    ╚██████╔╝      ██║  ██║███████╗███████╗██║     ███████╗██║  ╚██╗
     ╚═════╝       ╚═╝  ╚═╝╚══════╝╚══════╝╚═╝     ╚══════╝╚═╝   ╚═╝
                             ██╗     ██╗███╗   ██╗██╗   ██╗██╗  ██╗ 
                             ██║     ██║████╗  ██║██║   ██║╚██╗██╔╝ 
                             ██║     ██║██╔██╗ ██║██║   ██║ ╚███╔╝  
                             ██║     ██║██║╚██╗██║██║   ██║ ██╔██╗  
                             ███████╗██║██║ ╚████║╚██████╔╝██╔╝ ██╗ 
                             ╚══════╝╚═╝╚═╝  ╚═══╝ ╚═════╝ ╚═╝  ╚═╝ 
BANNER
echo "${RESET}"
echo "${DIM}    ░▒▓█████████████████████████████████████████████████████▓▒░${RESET}"
echo ""

if [[ "$MODE" == "uninstall" ]]; then
    echo "${RED}${BOLD}    ╔══════════════════════════════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}    ║${RESET}  ${BOLD}UNINSTALL SEQUENCE${RESET}                    ${DIM}rev 1.0${RESET}       ${RED}${BOLD}║${RESET}"
    echo "${RED}${BOLD}    ║${RESET}  ${DIM}PROTOCOL: TERMINATE → PURGE → CLEAN${RESET}                 ${RED}${BOLD}║${RESET}"
    echo "${RED}${BOLD}    ╚══════════════════════════════════════════════════════╝${RESET}"
elif [[ "$MODE" == "appimage" ]]; then
    echo "${YELLOW}${BOLD}    ╔══════════════════════════════════════════════════════╗${RESET}"
    echo "${YELLOW}${BOLD}    ║${RESET}  ${BOLD}APPIMAGE SUPPORT MODE${RESET}                 ${DIM}rev 1.0${RESET}       ${YELLOW}${BOLD}║${RESET}"
    echo "${YELLOW}${BOLD}    ║${RESET}  ${DIM}PROTOCOL: DOWNLOAD RULES → INJECT → ARM${RESET}             ${YELLOW}${BOLD}║${RESET}"
    echo "${YELLOW}${BOLD}    ╚══════════════════════════════════════════════════════╝${RESET}"
else
    echo "${CYAN}${BOLD}    ╔══════════════════════════════════════════════════════╗${RESET}"
    echo "${CYAN}${BOLD}    ║${RESET}  ${BOLD}REMOTE DEPLOYMENT SEQUENCE${RESET}            ${DIM}rev 1.0${RESET}       ${CYAN}${BOLD}║${RESET}"
    echo "${CYAN}${BOLD}    ║${RESET}  ${DIM}PROTOCOL: DOWNLOAD → VERIFY → INJECT → ARM${RESET}          ${CYAN}${BOLD}║${RESET}"
    echo "${CYAN}${BOLD}    ╚══════════════════════════════════════════════════════╝${RESET}"
fi
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

# ══════════════════════════════════════════════════════════════════════════════
#  UNINSTALL MODE
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "uninstall" ]]; then
    echo "${GREEN}  ▸ ROOT ACCESS${RESET} ${DIM}........................${RESET} ${GREEN}CONFIRMED${RESET}"
    echo "${GREEN}  ▸ USER${RESET} ${DIM}..............................${RESET} ${CYAN}${REAL_USER:-unknown}${RESET}"
    echo ""

    echo "${RED}${BOLD}  ╔══[ CONFIRMATION REQUIRED ]════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  This will remove G-Helper Linux and all system files."
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}User config (~/.config/ghelper) will NOT be removed.${RESET}"
    echo "${RED}${BOLD}  ╚═════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    printf "  ${BOLD}Type ${RED}YES${RESET}${BOLD} to confirm uninstall: ${RESET}"
    read -r confirm
    if [[ "$confirm" != "YES" ]]; then
        echo ""
        echo "  ${YELLOW}Aborted.${RESET}"
        exit 0
    fi
    echo ""

    # ── Stop running process ──
    _step 1 "TERMINATING RUNNING INSTANCES"
    if pgrep -x ghelper &>/dev/null; then
        pkill -x ghelper 2>/dev/null && _info "ghelper process terminated" || _warn "could not kill ghelper"
        sleep 0.5
    else
        _info "${DIM}no running ghelper process found${RESET}"
    fi

    # ── Remove files ──
    _step 2 "PURGING INSTALLED FILES"

    _safe_remove "$INSTALL_DIR"                         "install directory ($INSTALL_DIR)"
    _safe_remove "/usr/local/bin/ghelper"                "symlink"
    _safe_remove "$UDEV_DEST"                            "udev rules"
    _safe_remove "/etc/tmpfiles.d/90-ghelper.conf"       "tmpfiles config"
    _safe_remove "$DESKTOP_DEST"                         "desktop entry (system)"

    # User-local desktop entry
    if [[ -n "$REAL_USER" ]]; then
        _safe_remove "/home/$REAL_USER/.local/share/applications/ghelper.desktop" "desktop entry (user)"
        _safe_remove "/home/$REAL_USER/.config/autostart/ghelper.desktop"          "autostart entry"
    fi

    # Icons
    _safe_remove "/usr/share/icons/hicolor/64x64/apps/ghelper.png" "icon (system, png)"
    _safe_remove "/usr/share/icons/hicolor/64x64/apps/ghelper.ico" "icon (system, ico)"
    if [[ -n "$REAL_USER" ]]; then
        _safe_remove "/home/$REAL_USER/.local/share/icons/hicolor/64x64/apps/ghelper.png" "icon (user, png)"
        _safe_remove "/home/$REAL_USER/.local/share/icons/hicolor/64x64/apps/ghelper.ico" "icon (user, ico)"
    fi

    # ── Reload udev ──
    _step 3 "RELOADING UDEV DAEMON"
    udevadm control --reload-rules 2>/dev/null && _info "udev daemon reloaded" || true

    # ── Summary ──
    echo ""
    echo ""
    echo "${RED}${BOLD}  ╔════════════════════════════════════════════════════════════════╗${RESET}"
    echo "${RED}${BOLD}  ║                                                                ║${RESET}"
    echo "${RED}${BOLD}  ║  ▓▓▓ UNINSTALL COMPLETE ▓▓▓                                    ║${RESET}"
    echo "${RED}${BOLD}  ║                                                                ║${RESET}"
    echo "${RED}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${RED}REMOVED: $REMOVED files/directories${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}User config preserved at ~/.config/ghelper/${RESET}"
    echo "${RED}${BOLD}  ║${RESET}  ${DIM}sysfs permissions will reset on next reboot${RESET}"
    echo "${RED}${BOLD}  ║                                                                ║${RESET}"
    echo "${RED}${BOLD}  ╚════════════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    exit 0
fi

# ══════════════════════════════════════════════════════════════════════════════
#  INSTALL / APPIMAGE MODE — common setup
# ══════════════════════════════════════════════════════════════════════════════

echo "${GREEN}  ▸ ROOT ACCESS${RESET} ${DIM}........................${RESET} ${GREEN}CONFIRMED${RESET}"
echo "${GREEN}  ▸ USER${RESET} ${DIM}..............................${RESET} ${CYAN}${REAL_USER:-unknown}${RESET}"
if [[ "$MODE" == "install" ]]; then
    echo "${GREEN}  ▸ TARGET${RESET} ${DIM}............................${RESET} ${CYAN}$INSTALL_DIR/${RESET}"
fi
echo "${GREEN}  ▸ SOURCE${RESET} ${DIM}............................${RESET} ${CYAN}github.com/$REPO${RESET}"

# ── Temp workspace ─────────────────────────────────────────────────────────────
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

# ══════════════════════════════════════════════════════════════════════════════
#  [0x01] DOWNLOAD PAYLOADS
# ══════════════════════════════════════════════════════════════════════════════

_step 1 "DOWNLOADING PAYLOADS FROM REMOTE"

if [[ "$MODE" == "install" ]]; then
    BINARIES=(ghelper libSkiaSharp.so libHarfBuzzSharp.so)
else
    BINARIES=()
fi
ASSETS=(90-ghelper.rules 90-ghelper.conf)
if [[ "$MODE" == "install" ]]; then
    ASSETS+=(ghelper.desktop)
fi

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
if [[ "$MODE" == "install" ]]; then
    chmod +x "$WORK_DIR/ghelper"
fi
_info "All payloads acquired ${GREEN}(${dl_total} files)${RESET}"

# ══════════════════════════════════════════════════════════════════════════════
#  [0x02] INJECT BINARIES (install mode only)
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
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
else
    _info "${DIM}AppImage mode — skipping binary installation${RESET}"
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

# ── systemd-tmpfiles (for built-in kernel modules that udev can't handle) ──
TMPFILES_DEST="/etc/tmpfiles.d/90-ghelper.conf"
if [[ -f "$WORK_DIR/90-ghelper.conf" ]]; then
    _install_file "$WORK_DIR/90-ghelper.conf" "$TMPFILES_DEST" 644 "tmpfiles config" || true
    # Apply immediately so permissions take effect without reboot
    if command -v systemd-tmpfiles &>/dev/null; then
        systemd-tmpfiles --create "$TMPFILES_DEST" 2>/dev/null && \
            _info "systemd-tmpfiles applied — built-in module permissions set" || \
            _warn "systemd-tmpfiles --create had warnings (some paths may not exist yet)"
    else
        _warn "systemd-tmpfiles not found — permissions for built-in modules will require manual setup"
    fi
else
    _warn "90-ghelper.conf not found in download — skipping tmpfiles deployment"
fi

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
    /sys/class/leds/asus::kbd_backlight/multi_intensity \
    /sys/class/leds/asus::kbd_backlight/kbd_rgb_mode \
    /sys/class/leds/asus::kbd_backlight/kbd_rgb_state; do
    _ensure_chmod "$f"
done

# ── ASUS firmware-attributes (asus-armoury, kernel 6.8+) ──
_fa_count=0
for f in /sys/class/firmware-attributes/asus-armoury/attributes/*/current_value; do
    [[ -f "$f" ]] && { _ensure_chmod "$f"; ((_fa_count++)) || true; }
done
if [[ $_fa_count -gt 0 ]]; then
    _info "firmware-attributes (asus-armoury): ${GREEN}${_fa_count} attrs${RESET} processed"
else
    _info "${DIM}no asus-armoury firmware-attributes found (using legacy sysfs)${RESET}"
fi

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
#  [0x05] DESKTOP INTEGRATION (install mode only)
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
    _step 5 "DESKTOP INTEGRATION LAYER"

    if install -m 644 "$WORK_DIR/ghelper.desktop" "$DESKTOP_DEST" 2>/dev/null; then
        _inject "desktop entry → $DESKTOP_DEST"
    else
        _warn "desktop entry → $DESKTOP_DEST (read-only, using autostart instead)"
    fi
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x06] AUTOSTART IMPLANT (install mode only)
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
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
fi

# ══════════════════════════════════════════════════════════════════════════════
#  DEPLOYMENT COMPLETE
# ══════════════════════════════════════════════════════════════════════════════

echo ""
echo ""

if [[ "$MODE" == "appimage" ]]; then
    echo "${YELLOW}${BOLD}  ╔════════════════════════════════════════════════════════════════╗${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ║  ▓▓▓ APPIMAGE SUPPORT DEPLOYED ▓▓▓                             ║${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${CYAN}0xF0${RESET}  udev      → $UDEV_DEST"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${CYAN}0xF1${RESET}  tmpfiles  → /etc/tmpfiles.d/90-ghelper.conf"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${GREEN}INJECTED: $INJECTED${RESET}   ${CYAN}UPDATED: $UPDATED${RESET}   ${DIM}SKIPPED: $SKIPPED${RESET}"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${MAGENTA}CHMOD: $CHMOD_APPLIED armed${RESET}   ${DIM}$CHMOD_SKIPPED already set${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ╚════════════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    _typeout "${YELLOW}${BOLD}  > HARDWARE ACCESS LAYER READY :: Launch your AppImage now${RESET}" 0.03
else
    echo "${GREEN}${BOLD}  ╔════════════════════════════════════════════════════════════════╗${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ║  ▓▓▓ DEPLOYMENT SEQUENCE COMPLETE ▓▓▓                          ║${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF0${RESET}  Binary    → $INSTALL_DIR/ghelper"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF1${RESET}  Symlink   → /usr/local/bin/ghelper"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF2${RESET}  udev      → $UDEV_DEST"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF3${RESET}  tmpfiles  → /etc/tmpfiles.d/90-ghelper.conf"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF4${RESET}  Desktop   → $DESKTOP_DEST"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF5${RESET}  Autostart → ~/.config/autostart/ghelper.desktop"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ║${RESET}  ${GREEN}INJECTED: $INJECTED${RESET}   ${CYAN}UPDATED: $UPDATED${RESET}   ${DIM}SKIPPED: $SKIPPED${RESET}"
    echo "${GREEN}${BOLD}  ║${RESET}  ${MAGENTA}CHMOD: $CHMOD_APPLIED armed${RESET}   ${DIM}$CHMOD_SKIPPED already set${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ╚════════════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    _typeout "${GREEN}${BOLD}  > NEURAL LINK ESTABLISHED :: LAUNCH WITH: ghelper${RESET}" 0.03
fi

echo ""
