#!/usr/bin/env bash
# mouse-golden.sh - Write tests/GHelper.Linux.Tests/mouse-golden.txt from the Windows
# G-Helper mouse source. The C# tests run the same calls on the Linux port and compare.
#
# Run after ./scripts/mouse-port.sh syncs a new upstream copy.
# Requires: upstream G-Helper repo cloned at ../g-helper (relative to this repo root)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
UPSTREAM_DIR="$(cd "$REPO_ROOT/../g-helper" 2>/dev/null && pwd)" || { echo "ERROR: Upstream repo not found at ../g-helper"; exit 1; }

dotnet run --project "$SCRIPT_DIR/mouse-golden/MouseGolden.csproj" -c Release -- \
    "$REPO_ROOT/tests/GHelper.Linux.Tests/mouse-golden.txt" "$(git -C "$UPSTREAM_DIR" rev-parse --short HEAD)"
