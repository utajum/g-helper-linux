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

_install_desktop() {
    local src="$1" dest="$2" exec_val
    if [[ -n "${APPIMAGE:-}" && -f "${APPIMAGE:-}" ]]; then
        exec_val="$APPIMAGE"
    else
        exec_val="$INSTALL_DIR/ghelper"
    fi
    if [[ "$exec_val" == *" "* ]]; then
        exec_val="\"$exec_val\""
    fi
    sed "s|^Exec=.*|Exec=$exec_val|" "$src" > "$dest"
}

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
#  NixOS BRANCH (declarative: fetch module + binary, edit configuration.nix,
#  nixos-rebuild). NixOS /etc is read-only store symlinks, so the imperative
#  flow below cannot run here.
# ══════════════════════════════════════════════════════════════════════════════

GH_NIX_ROOT="/etc/nixos/ghelper"
GH_NIX_WRAPPER="/etc/nixos/ghelper.nix"
GH_NIX_CONF="/etc/nixos/configuration.nix"
RAW="https://raw.githubusercontent.com/$REPO/master"

_gh_nix_inject() {
    if grep -q '/etc/nixos/ghelper.nix' "$GH_NIX_CONF"; then
        return 0
    fi
    grep -qE '^[[:space:]]*imports[[:space:]]*=' "$GH_NIX_CONF" || return 1
    sed -i '0,/imports[[:space:]]*=/s|imports[[:space:]]*=|imports =\n    [ /etc/nixos/ghelper.nix ] ++|' "$GH_NIX_CONF"
}

_gh_nix_guidance() {
    echo ""
    _warn "Could not auto-edit $GH_NIX_CONF (no recognizable 'imports = [ ... ]')."
    echo "  ${DIM}Add this to your NixOS configuration, then 'nixos-rebuild switch':${RESET}"
    echo "  ${CYAN}imports = [ $GH_NIX_WRAPPER ];${RESET}"
    echo "  ${DIM}(${GH_NIX_WRAPPER} and ${GH_NIX_ROOT}/ were staged for you.)${RESET}"
}

_gh_nix_write_wrapper() {
    cat > "$GH_NIX_WRAPPER" <<EOF
# Managed by g-helper install script. Safe to delete (also remove the
# matching '[ /etc/nixos/ghelper.nix ] ++' from configuration.nix).
{ ... }:
{
  imports = [ ${GH_NIX_ROOT}/nixos/module.nix ];
  services.ghelper.enable = true;
}
EOF
}

_gh_nix_fetch() {
    local url="$1" dest="$2"
    curl -fsSL "$url" -o "$dest" || { _fail "fetch failed: $url"; return 1; }
}

_gh_nixos_rebuild() {
    _info "Validating configuration (nixos-rebuild dry-build)..."
    if ! nixos-rebuild dry-build 2>&1 | tail -3; then
        return 1
    fi
    _info "Applying (nixos-rebuild switch)..."
    nixos-rebuild switch 2>&1 | tail -5
}

if [[ -f /etc/NIXOS ]]; then
    _step 1 "NixOS DETECTED - DECLARATIVE DEPLOYMENT"

    if [[ "$MODE" == "uninstall" ]]; then
        if [[ -f "$GH_NIX_CONF" ]]; then
            cp "$GH_NIX_CONF" "${GH_NIX_CONF}.bak-ghelper-$(date +%s)"
            sed -i 's|\[ /etc/nixos/ghelper.nix \] ++ ||; s|\[ /etc/nixos/ghelper.nix \] ++||' "$GH_NIX_CONF"
            _remove "configuration.nix import"
        fi
        _safe_remove "$GH_NIX_WRAPPER" "ghelper.nix wrapper"
        _safe_remove "$GH_NIX_ROOT"    "staged module + binary"
        _info "Rebuilding without ghelper..."
        nixos-rebuild switch 2>&1 | tail -5
        echo ""
        _info "${GREEN}Uninstalled from NixOS.${RESET} ${DIM}User config preserved.${RESET}"
        exit 0
    fi

    _info "Fetching module + binary into ${GH_NIX_ROOT}/"
    rm -rf "$GH_NIX_ROOT"
    mkdir -p "$GH_NIX_ROOT/nixos" "$GH_NIX_ROOT/dist" \
             "$GH_NIX_ROOT/vendor/gpu-helper" "$GH_NIX_ROOT/install"

    _gh_nix_fetch "https://github.com/$REPO/releases/latest/download/ghelper" "$GH_NIX_ROOT/dist/ghelper" || exit 1
    chmod 755 "$GH_NIX_ROOT/dist/ghelper"
    _gh_nix_fetch "$RAW/nixos/module.nix"  "$GH_NIX_ROOT/nixos/module.nix"  || exit 1
    _gh_nix_fetch "$RAW/nixos/package.nix" "$GH_NIX_ROOT/nixos/package.nix" || exit 1
    # Full gpu-helper source tree; package.nix compiles all of it.
    for f in gpu-helper.c gpu-helper.h process_ops.c nvidia_ops.c \
             pci_ops.c wmi_ops.c ec_ops.c msr_ops.c lenovo_ops.c; do
        _gh_nix_fetch "$RAW/vendor/gpu-helper/$f" "$GH_NIX_ROOT/vendor/gpu-helper/$f" || exit 1
    done
    for f in 90-ghelper.rules gpu-block-helper.sh ghelper-gpu-boot.sh ghelper.desktop ghelper.png; do
        _gh_nix_fetch "$RAW/install/$f" "$GH_NIX_ROOT/install/$f" || exit 1
    done

    _gh_nix_write_wrapper
    _inject "wrapper → $GH_NIX_WRAPPER"

    if [[ ! -f "$GH_NIX_CONF" ]]; then
        _gh_nix_guidance
        exit 0
    fi

    cp "$GH_NIX_CONF" "${GH_NIX_CONF}.bak-ghelper-$(date +%s)"
    if ! _gh_nix_inject; then
        _gh_nix_guidance
        exit 0
    fi
    _inject "import → $GH_NIX_CONF"

    if _gh_nixos_rebuild; then
        echo ""
        _info "${GREEN}G-Helper deployed on NixOS.${RESET} Launch with: ${BOLD}ghelper${RESET}"
        exit 0
    else
        nix_bak=$(ls -t "${GH_NIX_CONF}".bak-ghelper-* 2>/dev/null | head -1)
        [[ -n "$nix_bak" ]] && cp "$nix_bak" "$GH_NIX_CONF"
        _fail "nixos-rebuild failed - configuration.nix restored from backup"
        _gh_nix_guidance
        exit 1
    fi
fi

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
    read -r confirm < /dev/tty
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

    # Disable systemd boot service BEFORE removing its unit file
    if systemctl is-enabled ghelper-gpu-boot.service 2>/dev/null; then
        systemctl disable ghelper-gpu-boot.service 2>/dev/null
        _info "ghelper-gpu-boot.service disabled"
    fi

    _safe_remove "$INSTALL_DIR"                          "install directory ($INSTALL_DIR)"
    _safe_remove "/usr/local/bin/ghelper"                 "symlink"
    _safe_remove "$UDEV_DEST"                             "udev rules"
    _safe_remove "/etc/modules-load.d/ghelper.conf"       "modules-load config"
    _safe_remove "/etc/modules-load.d/ghelper-lenovo.conf"    "modules-load config (legacy)"
    _safe_remove "/etc/systemd/system/ghelper-gpu-boot.service" "GPU boot systemd unit"
    _safe_remove "/usr/local/lib/ghelper"                 "ghelper lib directory"
    _safe_remove "/etc/sudoers.d/zz-ghelper-gpu"          "sudoers rule"
    _safe_remove "/etc/sudoers.d/ghelper-gpu"             "sudoers rule (legacy)"
    _safe_remove "/etc/modprobe.d/ghelper-gpu-block.conf"    "dGPU block (modprobe)"
    _safe_remove "/etc/udev/rules.d/50-ghelper-remove-dgpu.rules" "dGPU block (udev)"
    _safe_remove "/etc/ghelper"                           "ghelper config dir"
    _safe_remove "$DESKTOP_DEST"                          "desktop entry (system)"

    # Restore the NVIDIA Vulkan ICD if ghelper left it hidden (Eco mode)
    if [[ -f /usr/share/vulkan/icd.d/nvidia_icd.json_inactive && ! -f /usr/share/vulkan/icd.d/nvidia_icd.json ]]; then
        mv -f /usr/share/vulkan/icd.d/nvidia_icd.json_inactive /usr/share/vulkan/icd.d/nvidia_icd.json \
            && _info "restored NVIDIA Vulkan ICD" || true
    fi

    # User-local desktop entry
    if [[ -n "$REAL_USER" ]]; then
        _safe_remove "/home/$REAL_USER/.local/share/applications/ghelper.desktop" "desktop entry (user)"
        _safe_remove "/home/$REAL_USER/.config/autostart/ghelper.desktop"          "autostart entry"
    fi

    # Icons
    _safe_remove "/usr/share/icons/hicolor/256x256/apps/ghelper.png" "icon (system, png)"
    _safe_remove "/usr/share/icons/hicolor/64x64/apps/ghelper.png" "icon (system, png legacy)"
    _safe_remove "/usr/share/icons/hicolor/64x64/apps/ghelper.ico" "icon (system, ico legacy)"
    if [[ -n "$REAL_USER" ]]; then
        _safe_remove "/home/$REAL_USER/.local/share/icons/hicolor/256x256/apps/ghelper.png" "icon (user, png)"
        _safe_remove "/home/$REAL_USER/.local/share/icons/hicolor/64x64/apps/ghelper.png" "icon (user, png legacy)"
        _safe_remove "/home/$REAL_USER/.local/share/icons/hicolor/64x64/apps/ghelper.ico" "icon (user, ico legacy)"
    fi

    # ── Reload daemons ──
    _step 3 "RELOADING SYSTEM DAEMONS"
    systemctl daemon-reload 2>/dev/null && _info "systemd daemon-reload" || true
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
    BINARIES=(ghelper)
else
    BINARIES=()
fi
ASSETS=(90-ghelper.rules gpu-block-helper.sh ghelper-gpu-boot.sh ghelper-gpu-boot.service)
if [[ "$MODE" == "install" ]]; then
    ASSETS+=(ghelper.desktop ghelper.png)
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

if modprobe uinput 2>/dev/null; then
    _info "kernel module loaded → uinput"
else
    _warn "could not load kernel module → uinput (NumberPad virtual keyboard may be unavailable)"
fi

if modprobe i2c-dev 2>/dev/null; then
    _info "kernel module loaded → i2c-dev"
else
    _warn "could not load kernel module → i2c-dev (NumberPad LED control may be unavailable)"
fi

udevadm trigger
_info "udev trigger fired — re-applying all RUN commands"

# Keyboard remapper needs the input group to grab the integrated keyboard.
if [[ -n "${SUDO_USER:-}" ]] && ! id -nG "$SUDO_USER" | tr ' ' '\n' | grep -qx input; then
    usermod -aG input "$SUDO_USER" \
        && _inject "added $SUDO_USER to 'input' group (keyboard remapper)" \
        || _warn "could not add $SUDO_USER to input group"
fi

# ── Discrete-GPU detection (vendor-neutral, not ASUS-specific) ──
# Scan PCI for a discrete VGA/3D function: NVIDIA (0x10de), or AMD (0x1002) that
# is NOT boot_vga (so the APU/iGPU is excluded). The GPU boot service is only
# useful on machines that actually have a dGPU
has_discrete_gpu() {
    local d vendor cls bootvga
    for d in /sys/bus/pci/devices/*/; do
        [[ -r "$d/class" && -r "$d/vendor" ]] || continue
        cls=$(cat "$d/class" 2>/dev/null)
        [[ "$cls" == 0x0300* || "$cls" == 0x0302* ]] || continue
        vendor=$(cat "$d/vendor" 2>/dev/null)
        [[ "$vendor" == "0x10de" ]] && return 0
        if [[ "$vendor" == "0x1002" ]]; then
            bootvga=$(cat "$d/boot_vga" 2>/dev/null || echo 0)
            [[ "$bootvga" == "1" ]] || return 0
        fi
    done
    return 1
}

# ══════════════════════════════════════════════════════════════════════════════
#  [0x03b] GPU BLOCK HELPER + BOOT SERVICE + SUDOERS RULE
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
    _step 3 "GPU BLOCK HELPER + BOOT SERVICE + SUDOERS RULE"

    HELPER_DIR="/usr/local/lib/ghelper"
    HELPER_DEST="$HELPER_DIR/gpu-block-helper.sh"
    mkdir -p "$HELPER_DIR"

    _install_file "$WORK_DIR/gpu-block-helper.sh" "$HELPER_DEST" 755 "GPU block helper" || true

    # GPU boot service: only relevant on machines with a discrete GPU. On
    # iGPU/APU-only hardware it is a pure no-op, so don't install or enable it.
    if has_discrete_gpu; then
        GPU_BOOT_APPLIES=1
        _install_file "$WORK_DIR/ghelper-gpu-boot.sh" "$HELPER_DIR/ghelper-gpu-boot.sh" 755 "GPU boot script" || true
        _install_file "$WORK_DIR/ghelper-gpu-boot.service" "/etc/systemd/system/ghelper-gpu-boot.service" 644 "GPU boot systemd unit" || true
    else
        GPU_BOOT_APPLIES=0
        _skip "GPU boot service → no discrete GPU detected, skipping"
    fi

    # GPU-mode state dir — created for everyone so the boot service's
    # ReadWritePaths=/etc/ghelper can never fail on a missing dir.
    mkdir -p /etc/ghelper && chmod 0755 /etc/ghelper

    GPU_HELPER_DEST="$INSTALL_DIR/gpu-helper"
    if "$INSTALL_DIR/ghelper" --extract-helper gpu-helper "$GPU_HELPER_DEST" >/dev/null 2>&1; then
        chown root:root "$GPU_HELPER_DEST"
        chmod 755 "$GPU_HELPER_DEST"
        _inject "GPU privileged helper → $GPU_HELPER_DEST"
    else
        _warn "GPU helper extraction failed (GPU switching / holder detection unavailable)"
    fi

    # ryzenadj (upstream RyzenAdj CLI, embedded in ghelper): SMU power/temp
    # tuning. Only useful on AMD Ryzen APUs - skipped elsewhere.
    if grep -q "AuthenticAMD" /proc/cpuinfo && grep -qiE "Ryzen|Custom APU" /proc/cpuinfo; then
        RYZENADJ_DEST="$INSTALL_DIR/ryzenadj"
        if "$INSTALL_DIR/ghelper" --extract-helper ryzenadj "$RYZENADJ_DEST" >/dev/null 2>&1; then
            chown root:root "$RYZENADJ_DEST"
            chmod 755 "$RYZENADJ_DEST"
            _inject "ryzenadj (AMD SMU tuning) → $RYZENADJ_DEST"
        else
            _warn "ryzenadj extraction failed (AMD power tuning unavailable)"
        fi
    else
        _skip "ryzenadj → no AMD Ryzen CPU detected, skipping"
    fi

    # ryzenadj sudoers line applies when the CPU matches AND the binary
    # is present.
    RYZENADJ_APPLIES=0
    if grep -q "AuthenticAMD" /proc/cpuinfo && grep -qiE "Ryzen|Custom APU" /proc/cpuinfo \
       && [[ -x "$INSTALL_DIR/ryzenadj" ]]; then
        RYZENADJ_APPLIES=1
    fi

    # Sudoers rule — every privileged GPU operation now goes through the
    # root-owned helper binaries (gpu-helper validates each subcommand against
    # an internal whitelist, so this is no broader than per-command rules).
    SUDOERS_DEST="/etc/sudoers.d/zz-ghelper-gpu"
    SUDOERS_LEGACY="/etc/sudoers.d/ghelper-gpu"
    SUDOERS_CONTENT="# G-Helper: passwordless access to the root-owned helper binaries
ALL ALL=(root) NOPASSWD: $HELPER_DEST
ALL ALL=(root) NOPASSWD: /opt/ghelper/gpu-helper"
    if [[ "$RYZENADJ_APPLIES" == "1" ]]; then
        SUDOERS_CONTENT="$SUDOERS_CONTENT
ALL ALL=(root) NOPASSWD: /opt/ghelper/ryzenadj"
    fi

    if [[ -f "$SUDOERS_DEST" ]] && echo "$SUDOERS_CONTENT" | cmp -s - "$SUDOERS_DEST"; then
        _skip "sudoers rule → already deployed at $SUDOERS_DEST"
        rm -f "$SUDOERS_LEGACY"
    else
        echo "$SUDOERS_CONTENT" > "$SUDOERS_DEST"
        chmod 0440 "$SUDOERS_DEST"
        if visudo -c -f "$SUDOERS_DEST" &>/dev/null; then
            _inject "sudoers rule → $SUDOERS_DEST"
            rm -f "$SUDOERS_LEGACY"
        else
            rm -f "$SUDOERS_DEST"
            _fail "sudoers rule — syntax error, removed (GPU block will use pkexec fallback)"
        fi
    fi

    # Reload systemd and enable boot service
    systemctl daemon-reload 2>/dev/null || true
    _info "systemd daemon-reload"

    if [[ "${GPU_BOOT_APPLIES:-0}" == "1" ]]; then
        if systemctl enable ghelper-gpu-boot.service 2>/dev/null; then
            _info "ghelper-gpu-boot.service enabled"
        else
            _warn "failed to enable ghelper-gpu-boot.service (systemd may not be running)"
        fi
    fi
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x04] ESTABLISH SYSFS ACCESS LAYER
# ══════════════════════════════════════════════════════════════════════════════

_step 5 "ESTABLISHING SYSFS ACCESS LAYER"

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
    _step 6 "DESKTOP INTEGRATION LAYER"

    if _install_desktop "$WORK_DIR/ghelper.desktop" "$DESKTOP_DEST" 2>/dev/null; then
        chmod 644 "$DESKTOP_DEST" 2>/dev/null || true
        _inject "desktop entry → $DESKTOP_DEST"
    else
        _warn "desktop entry → $DESKTOP_DEST (read-only, using autostart instead)"
    fi

    # Application icon (256x256 PNG) into the hicolor theme so "Icon=ghelper"
    # resolves in the app grid / software centre and the launcher.
    if [[ -w "/usr/share/icons/hicolor/256x256/apps" ]] 2>/dev/null \
        || { [[ ! -e "/usr/share/icons/hicolor/256x256/apps" ]] && [[ -w "/usr/share/icons/hicolor" ]] 2>/dev/null; }; then
        ICON_BASE="/usr/share/icons/hicolor"
    else
        ICON_BASE="$HOME/.local/share/icons/hicolor"
    fi
    ICON_DEST="$ICON_BASE/256x256/apps"
    if [[ -f "$WORK_DIR/ghelper.png" ]]; then
        mkdir -p "$ICON_DEST" 2>/dev/null || true
        if install -m 644 "$WORK_DIR/ghelper.png" "$ICON_DEST/ghelper.png" 2>/dev/null; then
            _inject "icon → $ICON_DEST/ghelper.png"
            gtk-update-icon-cache -f -t "$ICON_BASE" 2>/dev/null || true
            update-desktop-database "$(dirname "$DESKTOP_DEST")" 2>/dev/null || true
        else
            _warn "icon → $ICON_DEST/ghelper.png (install failed)"
        fi
    fi
fi

# ══════════════════════════════════════════════════════════════════════════════
#  [0x06] AUTOSTART IMPLANT (install mode only)
# ══════════════════════════════════════════════════════════════════════════════

if [[ "$MODE" == "install" ]]; then
    _step 7 "AUTOSTART IMPLANT"

    if [[ -n "$REAL_USER" ]]; then
        AUTOSTART_DIR="/home/$REAL_USER/.config/autostart"
        AUTOSTART_DEST="$AUTOSTART_DIR/ghelper.desktop"
        # Create dir as the real user so ownership is correct from the start
        su -c "mkdir -p '$AUTOSTART_DIR'" "$REAL_USER"
        _install_desktop "$WORK_DIR/ghelper.desktop" "$AUTOSTART_DEST"
        chmod 644 "$AUTOSTART_DEST"
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
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${GREEN}INJECTED: $INJECTED${RESET}   ${CYAN}UPDATED: $UPDATED${RESET}   ${DIM}SKIPPED: $SKIPPED${RESET}"
    echo "${YELLOW}${BOLD}  ║${RESET}  ${MAGENTA}CHMOD: $CHMOD_APPLIED armed${RESET}   ${DIM}$CHMOD_SKIPPED already set${RESET}"
    echo "${YELLOW}${BOLD}  ║                                                                ║${RESET}"
    echo "${YELLOW}${BOLD}  ╚════════════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    _typeout "${YELLOW}${BOLD}  > HARDWARE ACCESS LAYER READY :: Launch your AppImage now${RESET}" 0.03
    echo ""
    echo "  ${DIM}To uninstall:${RESET}"
    echo "  ${DIM}curl -sL https://raw.githubusercontent.com/utajum/g-helper-linux/master/install/install.sh | sudo bash -s -- --uninstall${RESET}"
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
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF3${RESET}  Desktop   → $DESKTOP_DEST"
    echo "${GREEN}${BOLD}  ║${RESET}  ${CYAN}0xF4${RESET}  Autostart → ~/.config/autostart/ghelper.desktop"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ╠════════════════════════════════════════════════════════════════╣${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ║${RESET}  ${GREEN}INJECTED: $INJECTED${RESET}   ${CYAN}UPDATED: $UPDATED${RESET}   ${DIM}SKIPPED: $SKIPPED${RESET}"
    echo "${GREEN}${BOLD}  ║${RESET}  ${MAGENTA}CHMOD: $CHMOD_APPLIED armed${RESET}   ${DIM}$CHMOD_SKIPPED already set${RESET}"
    echo "${GREEN}${BOLD}  ║                                                                ║${RESET}"
    echo "${GREEN}${BOLD}  ╚════════════════════════════════════════════════════════════════╝${RESET}"
    echo ""
    _typeout "${GREEN}${BOLD}  > NEURAL LINK ESTABLISHED :: LAUNCH WITH: ghelper${RESET}" 0.03
    echo ""
    echo "  ${DIM}To uninstall:${RESET}"
    echo "  ${DIM}curl -sL https://raw.githubusercontent.com/utajum/g-helper-linux/master/install/install.sh | sudo bash -s -- --uninstall${RESET}"
fi

echo ""
