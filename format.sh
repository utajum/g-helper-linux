#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/src/GHelper.Linux.csproj"

echo "=== Format C# source files ==="
echo ""

dotnet format "$PROJECT" --verbosity normal

echo ""
echo "=== Format vendor + helper C source (excluding wlr-randr, rnnoise) ==="
if command -v clang-format >/dev/null 2>&1; then
    # bpf_blob.h is a generated hex dump of the compiled bpf object, not source.
    mapfile -t cfiles < <(find "$SCRIPT_DIR/vendor" "$SCRIPT_DIR/audio-helper" \( -name '*.c' -o -name '*.h' \) \
        -not -path '*/wlr-randr/*' -not -path '*/rnnoise/*' -not -name 'bpf_blob.h')
    if (( ${#cfiles[@]} )); then
        clang-format -i "${cfiles[@]}"
        printf '  formatted: %s\n' "${cfiles[@]#"$SCRIPT_DIR"/}"
    else
        echo "  no vendor C files to format"
    fi
else
    echo "  clang-format not found - skipping (install: sudo apt install clang-format)"
fi

echo ""
echo "Done."
