#!/usr/bin/env bash
# mouse-port.sh - Copy the Windows G-Helper mouse code into src/ and apply the Linux edits.
#
# The mouse code is a copy of Windows G-Helper, so an upstream change syncs by a new copy:
#   ./scripts/mouse-port.sh          # copy + edit, then review `git diff`
#   ./scripts/mouse-port.sh --check  # exit 1 when src/ differs from a fresh copy
#
# Each edit must match exactly once. When upstream changes a line that an edit needs,
# the script stops with the line, and the edit list below needs an update.
# Linux-only code lives outside the copied files (AsusMouse.Linux.cs, WindowsCompat.cs).
#
# Requires: upstream G-Helper repo cloned at ../g-helper (relative to this repo root)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
UPSTREAM_DIR="$(cd "$REPO_ROOT/../g-helper" 2>/dev/null && pwd)" || { echo "ERROR: Upstream repo not found at ../g-helper"; exit 1; }

python3 - "$UPSTREAM_DIR/app" "$REPO_ROOT/src" "${1:-}" <<'EOF'
import os, sys

up, src, mode = sys.argv[1], sys.argv[2], sys.argv[3]
check = mode == "--check"

# Windows-only: lighting of the Strix Arion SSD enclosure via a SCSI pass-through ioctl.
SKIP_MODELS = {"StrixArion.cs"}

ASUS_MOUSE = [
    ("using GHelper.AnimeMatrix.Communication;\nusing GHelper.AnimeMatrix.Communication.Platform;\nusing GHelper.USB;\n",
     "using GHelper.Linux.AnimeMatrix.Communication;\nusing GHelper.Linux.AnimeMatrix.Communication.Platform;\nusing GHelper.Linux.Helpers;\nusing GHelper.Linux.USB;\nusing System.Drawing;\n"),
    # Windows builds this file with these two nullable warnings
    ("namespace GHelper.Peripherals.Mouse\n", "#pragma warning disable CS8604, CS8625\n\nnamespace GHelper.Linux.Peripherals.Mouse\n"),
    ("public abstract class AsusMouse : Device, IPeripheral", "public abstract partial class AsusMouse : Device, IPeripheral"),
    ("_usbProvider = new WindowsUsbProvider(_vendorId, _productId, path, USBTimeout());",
     "_usbProvider = new LinuxUsbProvider(_vendorId, _productId, path, USBTimeout());"),
    (".FirstOrDefault(x => x.DevicePath.Contains(path)) != null;",
     ".FirstOrDefault(x => LinuxUsbProvider.PathMatches(x, path)) != null;"),
    ("x.ProductID == ProductID() && x.DevicePath.Contains(path));",
     "x.ProductID == ProductID() && LinuxUsbProvider.PathMatches(x, path));"),
]

EVENT_LISTENER = [
    ("using HidSharp;\n",
     "using GHelper.Linux.AnimeMatrix.Communication.Platform;\nusing GHelper.Linux.Helpers;\nusing HidSharp;\n"),
    ("namespace GHelper.Peripherals\n", "namespace GHelper.Linux.Peripherals\n"),
    ('if (device.DevicePath.Contains("mi_00")) continue;',
     'if (LinuxUsbProvider.PathMatches(device, "mi_00")) continue;'),
    ("if (commandPath is not null && device.DevicePath.Contains(commandPath)) continue;",
     "if (commandPath is not null && LinuxUsbProvider.PathMatches(device, commandPath)) continue;"),
]

MODEL = [
    ("namespace GHelper.Peripherals.Mouse.Models\n",
     "using GHelper.Linux.Helpers;\nusing System.Drawing;\n\nnamespace GHelper.Linux.Peripherals.Mouse.Models\n"),
]

def port(upstream, target, edits):
    with open(upstream, encoding="utf-8-sig") as f:
        s = f.read().replace("\r\n", "\n")
    for old, new in edits:
        n = s.count(old)
        if n != 1:
            sys.exit(f"ERROR: {os.path.relpath(upstream, up)}: edit matches {n} times, expected 1:\n  {old.strip()}")
        s = s.replace(old, new)
    if os.path.exists(target):
        with open(target, encoding="utf-8") as f:
            if f.read() == s:
                return False
    if check:
        print(f"differs: {os.path.relpath(target, src)}")
    else:
        with open(target, "w", encoding="utf-8", newline="\n") as f:
            f.write(s)
        print(f"updated: {os.path.relpath(target, src)}")
    return True

changed = port(f"{up}/Peripherals/Mouse/AsusMouse.cs", f"{src}/Peripherals/Mouse/AsusMouse.cs", ASUS_MOUSE)
changed |= port(f"{up}/Peripherals/PeripheralEventListener.cs", f"{src}/Peripherals/PeripheralEventListener.cs", EVENT_LISTENER)

models = sorted(f for f in os.listdir(f"{up}/Peripherals/Mouse/Models") if f.endswith(".cs") and f not in SKIP_MODELS)
for name in models:
    changed |= port(f"{up}/Peripherals/Mouse/Models/{name}", f"{src}/Peripherals/Mouse/Models/{name}", MODEL)
for name in sorted(set(os.listdir(f"{src}/Peripherals/Mouse/Models")) - set(models)):
    print(f"WARNING: {name} is not in upstream any more")

if check and changed:
    sys.exit(1)
print("mouse code matches upstream" if not changed else "done, review git diff")
EOF
