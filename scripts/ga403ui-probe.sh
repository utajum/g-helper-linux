#!/usr/bin/env bash
set -u

OUT="${1:-ga403ui-probe-$(date +%Y%m%d-%H%M%S).txt}"

section() {
  printf '\n\n===== %s =====\n' "$1" >> "$OUT"
}

run() {
  printf '\n$ %s\n' "$*" >> "$OUT"
  "$@" >> "$OUT" 2>&1 || true
}

: > "$OUT"
printf 'G-Helper Linux GA403UI read-only hardware probe\n' >> "$OUT"
printf 'Generated: %s\n' "$(date --iso-8601=seconds 2>/dev/null || date)" >> "$OUT"
printf 'No firmware/sysfs values are written by this script.\n' >> "$OUT"

section "DMI"
for f in sys_vendor product_name product_version board_name board_version bios_vendor bios_version bios_date; do
  if [[ -r "/sys/class/dmi/id/$f" ]]; then
    printf '%-18s: %s\n' "$f" "$(cat "/sys/class/dmi/id/$f")" >> "$OUT"
  fi
done

section "OS / kernel / session"
run uname -a
if [[ -r /etc/os-release ]]; then
  run cat /etc/os-release
fi
printf '\nXDG_CURRENT_DESKTOP=%s\n' "${XDG_CURRENT_DESKTOP:-}" >> "$OUT"
printf 'XDG_SESSION_TYPE=%s\n' "${XDG_SESSION_TYPE:-}" >> "$OUT"
printf 'WAYLAND_DISPLAY=%s\n' "${WAYLAND_DISPLAY:-}" >> "$OUT"
printf 'DISPLAY=%s\n' "${DISPLAY:-}" >> "$OUT"

section "ASUS kernel modules"
run sh -c "lsmod | grep -E 'asus|wmi|hid_asus|nvidia|amdgpu|ryzen_smu' || true"

section "ASUS platform devices"
run sh -c "find /sys/bus/platform/devices /sys/devices/platform -maxdepth 2 \
  \( -iname '*asus*' -o -iname '*rog*' \) -print 2>/dev/null | sort -u"

section "asus-armoury firmware attributes"
if [[ -d /sys/class/firmware-attributes/asus-armoury/attributes ]]; then
  while IFS= read -r attr; do
    printf '\n[%s]\n' "$attr" >> "$OUT"
    for f in current_value default_value min_value max_value possible_values display_name; do
      p="/sys/class/firmware-attributes/asus-armoury/attributes/$attr/$f"
      [[ -r "$p" ]] && printf '%-16s %s\n' "$f:" "$(cat "$p" 2>/dev/null)" >> "$OUT"
    done
  done < <(find /sys/class/firmware-attributes/asus-armoury/attributes -mindepth 1 -maxdepth 1 -type d -printf '%f\n' 2>/dev/null | sort)
else
  printf 'asus-armoury firmware attributes directory not present.\n' >> "$OUT"
fi

section "Legacy ASUS WMI controls"
for base in /sys/devices/platform/asus-nb-wmi /sys/bus/platform/devices/asus-nb-wmi; do
  [[ -d "$base" ]] || continue
  printf '\nBase: %s\n' "$base" >> "$OUT"
  for f in dgpu_disable gpu_mux_mode charge_control_end_threshold throttle_thermal_policy panel_od; do
    [[ -r "$base/$f" ]] && printf '%-32s %s\n' "$f:" "$(cat "$base/$f" 2>/dev/null)" >> "$OUT"
  done
done

section "PCI GPUs and runtime power"
if command -v lspci >/dev/null 2>&1; then
  run sh -c "lspci -Dnnk | grep -A4 -E 'VGA compatible controller|3D controller|Display controller'"
fi
for dev in /sys/bus/pci/devices/*; do
  [[ -r "$dev/vendor" ]] || continue
  vendor="$(cat "$dev/vendor" 2>/dev/null)"
  if [[ "$vendor" == "0x10de" || "$vendor" == "0x1002" ]]; then
    printf '\nPCI %s vendor=%s\n' "$(basename "$dev")" "$vendor" >> "$OUT"
    for f in device class power/runtime_status power/control power_state; do
      [[ -r "$dev/$f" ]] && printf '%-24s %s\n' "$f:" "$(cat "$dev/$f" 2>/dev/null)" >> "$OUT"
    done
    [[ -L "$dev/driver" ]] && printf '%-24s %s\n' "driver:" "$(basename "$(readlink "$dev/driver")")" >> "$OUT"
  fi
done

section "hwmon fans / temperatures"
for hw in /sys/class/hwmon/hwmon*; do
  [[ -d "$hw" ]] || continue
  name="$(cat "$hw/name" 2>/dev/null || echo unknown)"
  printf '\n[%s] %s\n' "$(basename "$hw")" "$name" >> "$OUT"
  for f in "$hw"/fan*_input "$hw"/fan*_label "$hw"/temp*_input "$hw"/temp*_label; do
    [[ -r "$f" ]] || continue
    printf '%-30s %s\n' "$(basename "$f"):" "$(cat "$f" 2>/dev/null)" >> "$OUT"
  done
done

section "Battery"
for bat in /sys/class/power_supply/BAT*; do
  [[ -d "$bat" ]] || continue
  printf '\n[%s]\n' "$(basename "$bat")" >> "$OUT"
  for f in status capacity charge_control_end_threshold charge_control_start_threshold energy_full energy_full_design cycle_count; do
    [[ -r "$bat/$f" ]] && printf '%-32s %s\n' "$f:" "$(cat "$bat/$f" 2>/dev/null)" >> "$OUT"
  done
done

section "LED classes"
for led in /sys/class/leds/*; do
  [[ -d "$led" ]] || continue
  printf '\n[%s]\n' "$(basename "$led")" >> "$OUT"
  for f in brightness max_brightness trigger; do
    [[ -r "$led/$f" ]] && printf '%-16s %s\n' "$f:" "$(cat "$led/$f" 2>/dev/null)" >> "$OUT"
  done
done

section "HID / hidraw interfaces"
for h in /sys/class/hidraw/hidraw*; do
  [[ -e "$h" ]] || continue
  printf '\n[%s]\n' "$(basename "$h")" >> "$OUT"
  uevent="$h/device/uevent"
  if [[ -r "$uevent" ]]; then
    grep -E '^(HID_ID|HID_NAME|HID_PHYS|MODALIAS)=' "$uevent" >> "$OUT" 2>/dev/null || true
  fi
  real="$(readlink -f "$h/device" 2>/dev/null || true)"
  [[ -n "$real" ]] && printf 'SYSFS=%s\n' "$real" >> "$OUT"
done

section "Ryzen SMU / Curve Optimizer"
for p in /dev/ryzen_smu_drv /sys/kernel/ryzen_smu_drv /sys/module/ryzen_smu; do
  if [[ -e "$p" ]]; then
    printf 'present: %s\n' "$p" >> "$OUT"
  else
    printf 'missing: %s\n' "$p" >> "$OUT"
  fi
done

section "Display connectors"
for c in /sys/class/drm/card*-*/status; do
  [[ -r "$c" ]] || continue
  dir="$(dirname "$c")"
  printf '\n[%s] status=%s\n' "$(basename "$dir")" "$(cat "$c" 2>/dev/null)" >> "$OUT"
  [[ -r "$dir/modes" ]] && sed 's/^/mode: /' "$dir/modes" >> "$OUT"
done

if command -v xrandr >/dev/null 2>&1 && [[ -n "${DISPLAY:-}" ]]; then
  section "xrandr"
  run xrandr --current
fi

if command -v wlr-randr >/dev/null 2>&1 && [[ -n "${WAYLAND_DISPLAY:-}" ]]; then
  section "wlr-randr"
  run wlr-randr
fi

section "G-Helper config presence"
if [[ -r "$HOME/.config/ghelper/config.json" ]]; then
  printf 'present: %s\n' "$HOME/.config/ghelper/config.json" >> "$OUT"
else
  printf 'missing: %s\n' "$HOME/.config/ghelper/config.json" >> "$OUT"
fi

printf '\nProbe complete: %s\n' "$OUT"
