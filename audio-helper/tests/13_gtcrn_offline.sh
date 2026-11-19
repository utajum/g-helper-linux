#!/usr/bin/env bash
# Phase 13: denoise model choice (GTCRN default, RNNoise), checked offline on
# the real DSP code (no PipeWire session needed).
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

if ! python3 -c 'import numpy' 2>/dev/null; then
    echo "SKIP: python3 numpy not installed" >&2
    exit 0
fi

(cd "$ROOT" && make -s render) || { echo "render build failed" >&2; exit 1; }
python3 "$HERE/gtcrn_offline.py" "$ROOT/render"
