#!/usr/bin/env bash
set -uo pipefail

# G-Helper Linux — Build Script
# Compiles the project as a native AOT binary and copies output to dist/

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$SCRIPT_DIR/src"
DIST_DIR="$SCRIPT_DIR/dist"
PUBLISH_DIR="$SRC_DIR/bin/Release/net10.0/linux-x64/publish"

USE_AOT=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-aot|--fast|-f)
            USE_AOT=0
            ;;
        -h|--help)
            cat <<EOF
Usage: $0 [--no-aot|--fast|-f]

Modes:
  (default)            Full Native AOT build. Single ~50MB binary
                       in dist/ghelper. Takes ~2 min.
  --no-aot, --fast,-f  Fast iteration build. Skips AOT/trimming. dist/
                       becomes a folder (~80MB) containing ghelper + DLLs.
                       Incremental rebuilds (~5-10s after first run).

Other flags:
  -h, --help           Show this message.
EOF
            exit 0
            ;;
        *)
            echo "Unknown flag: $1" >&2
            echo "Run '$0 --help' for usage." >&2
            exit 2
            ;;
    esac
    shift
done

echo "=== G-Helper Linux Build ==="
if (( USE_AOT )); then
    echo "    Mode: Native AOT"
else
    echo "    Mode: Fast (no AOT, no trim)"
fi
echo ""

# Check .NET SDK
if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET SDK not found."
    echo "Install it with:"
    echo "  Ubuntu/Debian:  sudo apt install dotnet-sdk-10.0"
    echo "  Fedora:         sudo dnf install dotnet-sdk-10.0"
    echo "  Arch:           sudo pacman -S dotnet-sdk"
    exit 1
fi

SDK_VERSION=$(dotnet --version 2>/dev/null || echo "unknown")
echo "Using .NET SDK: $SDK_VERSION"

# Check for clang (required for AOT)
if ! command -v clang &>/dev/null; then
    echo ""
    echo "WARNING: clang not found. Native AOT requires clang."
    echo "Install it with:"
    echo "  Ubuntu/Debian:  sudo apt install clang zlib1g-dev"
    echo "  Fedora:         sudo dnf install clang zlib-devel"
    echo "  Arch:           sudo pacman -S clang"
    echo ""
    read -rp "Try building anyway? [y/N] " ans
    [[ "$ans" =~ ^[Yy] ]] || exit 1
fi

# Build ghelper-audio (PipeWire audio helper for noise suppression + DSP chain).
# Compiled as a tiny native helper, embedded into the AOT binary, extracted
# at runtime by NativeLibExtractor. Vendored rnnoise (BSD-3) is GPL-3 compatible.
AUDIO_HELPER_DIR="$SCRIPT_DIR/audio-helper"
AUDIO_HELPER_BIN=""

if ! command -v pkg-config &>/dev/null || \
   ! pkg-config --exists libpipewire-0.3 2>/dev/null || \
   ! command -v cc &>/dev/null; then
    echo ""
    echo "ERROR: Cannot build ghelper-audio — libpipewire-0.3 dev headers not found."
    echo "  Install with:"
    echo "    Ubuntu/Debian: sudo apt install libpipewire-0.3-dev pkg-config"
    echo "    Fedora:        sudo dnf install pipewire-devel pkg-config"
    echo "    Arch:          sudo pacman -S libpipewire pkg-config"
    exit 1
fi

echo ""
echo "Building ghelper-audio (PipeWire helper)..."
(
    cd "$AUDIO_HELPER_DIR"
    make clean >/dev/null 2>&1
    make -j"$(nproc 2>/dev/null || echo 2)"
)
if [[ -f "$AUDIO_HELPER_DIR/ghelper-audio" ]]; then
    AUDIO_HELPER_BIN="$AUDIO_HELPER_DIR/ghelper-audio"
    echo "  ghelper-audio built: $(du -sh "$AUDIO_HELPER_BIN" | cut -f1)"
else
    echo ""
    echo "ERROR: ghelper-audio build failed — see make output above."
    exit 1
fi

# Build wlr-randr (Wayland display tool — vendored v0.5.0, MIT license)
WLR_RANDR_DIR="$SCRIPT_DIR/vendor/wlr-randr"
WLR_RANDR_BIN=""

if command -v wayland-scanner &>/dev/null && command -v cc &>/dev/null; then
    WLR_VERSION=$(cat "$WLR_RANDR_DIR/VERSION" 2>/dev/null || echo "unknown")
    echo ""
    echo "Building wlr-randr v${WLR_VERSION}..."
    (
        cd "$WLR_RANDR_DIR"
        wayland-scanner client-header \
            protocol/wlr-output-management-unstable-v1.xml \
            wlr-output-management-unstable-v1-client-protocol.h
        wayland-scanner private-code \
            protocol/wlr-output-management-unstable-v1.xml \
            wlr-output-management-unstable-v1-protocol.c
        cc -O2 -o wlr-randr main.c wlr-output-management-unstable-v1-protocol.c \
            -I. -lwayland-client -lm
        strip wlr-randr
    )
    if [[ -f "$WLR_RANDR_DIR/wlr-randr" ]]; then
        WLR_RANDR_BIN="$WLR_RANDR_DIR/wlr-randr"
        echo "  wlr-randr built: $(du -sh "$WLR_RANDR_BIN" | cut -f1)"
    else
        echo "WARNING: wlr-randr build failed (Wayland refresh rate switching unavailable)"
    fi
else
    echo ""
    echo "NOTE: wayland-scanner not found, skipping wlr-randr build."
    echo "  Install with: sudo apt install libwayland-dev"
fi

# Build gpu-helper (root-only privileged GPU operations multiplexer, vendored)
GPU_HELPER_DIR="$SCRIPT_DIR/vendor/gpu-helper"
GPU_HELPER_BIN=""

if command -v cc &>/dev/null; then
    echo ""
    echo "Building gpu-helper..."
    (
        cd "$GPU_HELPER_DIR"

        # Optional eBPF filter: needs clang with a bpf target.
        BPF_DEFINE=""
        rm -f bpf_blob.h
        if command -v clang &>/dev/null && clang -print-targets 2>/dev/null | grep -q bpf; then
            if clang -O2 -g -Wall -target bpf -D__TARGET_ARCH_x86 \
                     -c bpf/ghelper_gpublock.bpf.c -o bpf/ghelper_gpublock.bpf.o 2>/dev/null; then
                # Embed as a C array so gpu-helper stays a single binary.
                if command -v xxd &>/dev/null; then
                    xxd -i -n gh_bpf_obj bpf/ghelper_gpublock.bpf.o > bpf_blob.h
                else
                    # Portable fallback for hosts without xxd (vim-less images).
                    {
                        printf 'unsigned char gh_bpf_obj[] = {\n'
                        od -An -tx1 -v bpf/ghelper_gpublock.bpf.o \
                            | awk '{for(i=1;i<=NF;i++) printf "0x%s,", $i; print ""}'
                        printf '};\nunsigned int gh_bpf_obj_len = %s;\n' \
                            "$(stat -c%s bpf/ghelper_gpublock.bpf.o)"
                    } > bpf_blob.h
                fi
                BPF_DEFINE="-DGH_HAVE_BPF_BLOB"
                echo "  gpu block filter built: $(du -sh bpf/ghelper_gpublock.bpf.o | cut -f1)"
            else
                echo "  NOTE: bpf filter compile failed, eBPF GPU block disabled."
            fi
        else
            echo "  NOTE: clang with bpf target not found, eBPF GPU block disabled."
        fi

        HELPER_SRCS="process_ops.c nvidia_ops.c pci_ops.c \
                     wmi_ops.c ec_ops.c msr_ops.c lenovo_ops.c bpf_ops.c lsm_ops.c"
        cc -O2 -Wall -Wno-unused-result -DNDEBUG $BPF_DEFINE \
           -o gpu-helper gpu-helper.c $HELPER_SRCS \
           -ldl
        strip gpu-helper
    )
    if [[ -f "$GPU_HELPER_DIR/gpu-helper" ]]; then
        GPU_HELPER_BIN="$GPU_HELPER_DIR/gpu-helper"
        echo "  gpu-helper built: $(du -sh "$GPU_HELPER_BIN" | cut -f1)"
    else
        echo "ERROR: gpu-helper build failed (privileged GPU operations unavailable)"
        echo "  Check: 'cc' is installed"
        exit 1
    fi
else
    echo ""
    echo "NOTE: cc not found, skipping gpu-helper build."
fi

# Clean previous build artifacts. Skipped in fast mode so MSBuild's
# up-to-date check can shortcut unchanged work on repeat runs.
echo ""
if (( USE_AOT )); then
    echo "[1/4] Cleaning previous build..."
    rm -rf "$SRC_DIR/bin/Release" 2>/dev/null || true
else
    echo "[1/4] Cleaning (fast mode) to force MSBuild condition re-evaluation..."
    rm -rf "$SRC_DIR/bin/Release" "$SRC_DIR/obj/Release" 2>/dev/null || true
fi

# Restore packages
echo "[2/4] Restoring packages..."
if ! dotnet restore "$SRC_DIR" --runtime linux-x64 -q; then
    echo "ERROR: Package restore failed."
    exit 1
fi

# Prepare native .so for embedding
EMBED_DIR="$SCRIPT_DIR/build/embedded"
rm -rf "$EMBED_DIR"
mkdir -p "$EMBED_DIR"

NUGET_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
ASSETS_FILE="$SRC_DIR/obj/project.assets.json"

# Read the version the restore above actually resolved for a package.
# The NuGet cache is shared across every project on the machine, so it can hold
# newer versions than this project uses. Picking the newest directory embeds a
# native library the managed assembly refuses to load: SkiaSharp gates on the
# native major and aborts at startup with an "incompatible" error.
resolved_pkg_version() {
    local pkg_esc
    pkg_esc=$(printf '%s' "$1" | sed 's/\./\\./g')
    sed -n "s|^ *\"$pkg_esc/\([^\"]*\)\": {.*|\1|p" "$ASSETS_FILE" 2>/dev/null | sort -u
}

if [[ ! -f "$ASSETS_FILE" ]]; then
    echo "ERROR: $ASSETS_FILE not found; cannot tell which native libraries to embed."
    exit 1
fi

for lib_spec in \
    "libSkiaSharp.so:SkiaSharp.NativeAssets.Linux:runtimes/linux-x64/native/libSkiaSharp.so" \
    "libHarfBuzzSharp.so:HarfBuzzSharp.NativeAssets.Linux:runtimes/linux-x64/native/libHarfBuzzSharp.so"; do
    IFS=':' read -r lib_name pkg_name pkg_path <<< "$lib_spec"

    pkg_ver=$(resolved_pkg_version "$pkg_name")
    if [[ -z "$pkg_ver" ]]; then
        echo "ERROR: $pkg_name is not in $ASSETS_FILE."
        echo "       Cannot embed $lib_name. Check the package references in src/."
        exit 1
    fi
    if [[ $(wc -l <<< "$pkg_ver") -ne 1 ]]; then
        echo "ERROR: $pkg_name resolved to multiple versions:" $pkg_ver
        echo "       Refusing to guess which native $lib_name to embed."
        exit 1
    fi

    # The restore writes into NUGET_PACKAGES, but read-only fallback folders can
    # supply a package too, so check those before giving up. Fallback folders are
    # colon separated.
    src_so=""
    IFS=':' read -r -a fallback_roots <<< "${NUGET_FALLBACK_PACKAGES:-}"
    for root in "$NUGET_DIR" ${fallback_roots[@]+"${fallback_roots[@]}"}; do
        [[ -n "$root" ]] || continue
        cand="$root/${pkg_name,,}/$pkg_ver/$pkg_path"
        if [[ -f "$cand" ]]; then
            src_so="$cand"
            break
        fi
    done
    if [[ -z "$src_so" ]]; then
        echo "ERROR: $lib_name not found for resolved $pkg_name $pkg_ver."
        echo "       Looked under $NUGET_DIR/${pkg_name,,}/$pkg_ver/."
        exit 1
    fi

    cp "$src_so" "$EMBED_DIR/$lib_name"
    strip --strip-unneeded "$EMBED_DIR/$lib_name" 2>/dev/null || true
    echo "  Embedded $lib_name: $(du -sh "$EMBED_DIR/$lib_name" | cut -f1) (stripped, $pkg_name $pkg_ver)"
done

# Embed ghelper-audio helper if it was built
if [[ -n "$AUDIO_HELPER_BIN" && -f "$AUDIO_HELPER_BIN" ]]; then
    cp "$AUDIO_HELPER_BIN" "$EMBED_DIR/ghelper-audio"
    echo "  Embedded ghelper-audio: $(du -sh "$EMBED_DIR/ghelper-audio" | cut -f1)"
fi

# Publish
if (( USE_AOT )); then
    echo "[3/4] Compiling native AOT binary (this may take a minute)..."
    dotnet publish "$SRC_DIR" -c Release --no-restore 2>&1 \
        | grep -v "^.*error : Deleting file" || true
else
    echo "[3/4] Compiling (fast mode, no AOT)..."
    dotnet publish "$SRC_DIR" -c Release --no-restore \
        -p:PublishAot=false \
        -p:PublishTrimmed=false \
        -p:StripSymbols=false \
        --self-contained true -r linux-x64 2>&1 \
        | grep -v "^.*error : Deleting file" || true
fi

# Verify the binary was produced
if [[ ! -f "$PUBLISH_DIR/ghelper" ]]; then
    echo ""
    echo "ERROR: Build failed — binary not found at $PUBLISH_DIR/ghelper"
    echo "Run 'dotnet publish src/ -c Release' manually to see full errors."
    exit 1
fi

# Copy to dist/
echo "[4/4] Copying to dist/..."
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

if (( USE_AOT )); then
    cp "$PUBLISH_DIR/ghelper" "$DIST_DIR/"
else
    cp -r "$PUBLISH_DIR/." "$DIST_DIR/"
fi
chmod +x "$DIST_DIR/ghelper"

# Clean wlr-randr build artifacts from vendor dir (binary is embedded in ghelper)
if [[ -n "$WLR_RANDR_BIN" ]]; then
    rm -f "$WLR_RANDR_DIR/wlr-randr" \
          "$WLR_RANDR_DIR/wlr-output-management-unstable-v1-client-protocol.h" \
          "$WLR_RANDR_DIR/wlr-output-management-unstable-v1-protocol.c"
fi

# Clean ghelper-audio build artifacts (binary is embedded)
if [[ -n "$AUDIO_HELPER_BIN" ]]; then
    (cd "$AUDIO_HELPER_DIR" && make clean >/dev/null 2>&1) || true
fi

# Clean gpu-helper build artifact from vendor dir (binary is embedded in ghelper)
if [[ -n "$GPU_HELPER_BIN" ]]; then
    rm -f "$GPU_HELPER_DIR/gpu-helper"
    rm -f "$GPU_HELPER_DIR/bpf_blob.h"
    rm -f "$GPU_HELPER_DIR/bpf/ghelper_gpublock.bpf.o"
fi

# Summary
BINARY_SIZE=$(du -sh "$DIST_DIR/ghelper" | cut -f1)
TOTAL_SIZE=$(du -sh "$DIST_DIR" | cut -f1)
FILE_COUNT=$(ls -1 "$DIST_DIR" | wc -l)

echo ""
echo "=== Build Complete ==="
if (( USE_AOT )); then
    echo "  Mode:    Native AOT (single binary)"
else
    echo "  Mode:    Fast (no AOT, folder output)"
fi
echo "  Binary:  $BINARY_SIZE  (ghelper)"
echo "  Total:   $TOTAL_SIZE  ($FILE_COUNT files)"
echo "  Output:  $DIST_DIR/"
echo ""
echo "Run it:"
echo "  $DIST_DIR/ghelper"
