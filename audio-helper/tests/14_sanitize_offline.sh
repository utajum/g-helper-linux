#!/usr/bin/env bash
# Phase 14: offline DSP on a 150 Hz tone under ASan and UBSan; any report fails.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

SAN="-fsanitize=address,undefined -fno-sanitize-recover=all"
cp -r "$ROOT/." "$TMP/"
(cd "$TMP" && make -s clean && make -s render CFLAGS="-O1 -g -fno-strict-aliasing -I. -DRNNOISE_BUILD $SAN" LDFLAGS="$SAN") ||
    { echo "render build failed" >&2; exit 1; }

python3 -c 'import array, math, sys; sys.stdout.buffer.write(array.array("f", (0.3 * math.sin(2 * math.pi * 150 * i / 48000) for i in range(96000))).tobytes())' > "$TMP/in.f32"
if "$TMP/render" "RNN 0" < "$TMP/in.f32" > /dev/null; then
    echo "ok   render is clean under ASan and UBSan"
else
    echo "FAIL render under ASan and UBSan, see the report above"
    exit 1
fi
