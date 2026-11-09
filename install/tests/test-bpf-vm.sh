#!/bin/bash
# Boots a stock Ubuntu kernel in qemu and runs the eBPF GPU block for real:
# verifier, attach, deny rules, env/forced/pid records, listings, and the
# lockdown=integrity fallback. Two fake GPUs stand in for the iGPU and dGPU.
#
# Usage: test-bpf-vm.sh [vmlinuz modules-dir]
#   Without arguments the newest Ubuntu generic kernel is fetched with
#   apt-get download and cached under ~/.cache/ghelper-bpf-vm.
# Needs: qemu-system-x86_64, clang with the bpf target, cc, busybox (static),
#        libbpf.so.1, cpio, zstd, xxd.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
SRC="$HERE/../../vendor/gpu-helper"
VM="$HERE/bpf-vm"

if [[ $# -ge 2 ]]; then
    KERNEL=$1
    MODDIR=$2
else
    CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/ghelper-bpf-vm"
    pkg=$(apt-cache search --names-only '^linux-image-unsigned-[0-9.]+-[0-9]+-generic$' \
        | awk '{print $1}' | sort -V | tail -1)
    ver=${pkg#linux-image-unsigned-}
    if [[ ! -d "$CACHE/$ver" ]]; then
        mkdir -p "$CACHE/dl"
        (cd "$CACHE/dl" && apt-get download "$pkg" "linux-modules-$ver" >/dev/null)
        for deb in "$CACHE"/dl/*.deb; do dpkg-deb -x "$deb" "$CACHE/$ver"; done
        rm -rf "$CACHE/dl"
    fi
    KERNEL="$CACHE/$ver/boot/vmlinuz-$ver"
    MODDIR="$CACHE/$ver/lib/modules/$ver"
fi

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT

# Build from a copy: a bpf_blob.h left by build.sh would shadow the fresh one.
mkdir -p "$W/src/bpf"
cp "$SRC"/*.c "$SRC"/*.h "$W/src/"
cp "$SRC"/bpf/*.c "$SRC"/bpf/*.h "$W/src/bpf/"
clang -O2 -g -Wall -Werror -target bpf -D__TARGET_ARCH_x86 -c "$W/src/bpf/ghelper_gpublock.bpf.c" -o "$W/f.o"
(cd "$W" && cp f.o gh && xxd -i -n gh_bpf_obj gh > src/bpf_blob.h)
(cd "$W/src" && cc -O2 -Wall -Werror -Wno-unused-result -DNDEBUG -DGH_HAVE_BPF_BLOB \
    -o "$W/gpu-helper" *.c -ldl)

R="$W/root"
mkdir -p "$R"/{bin,proc,sys,dev,tmp,run,etc,mods,usr/local/lib/ghelper}
cp "$(command -v busybox)" "$R/bin/busybox"
for a in sh cat ls sleep mount umount insmod grep printf tr poweroff readlink basename dirname head sed; do
    ln -sf busybox "$R/bin/$a"
done
cp "$W/gpu-helper" "$R/usr/local/lib/ghelper/gpu-helper"
cc -static -O2 -o "$R/bin/probe" "$VM/probe.c"
cp "$R/bin/probe" "$R/bin/launcher"
cc -O2 -o "$R/bin/dbgload" "$VM/dbgload.c" -ldl
cp "$W/f.o" "$R/f.o"
libbpf=$(ldconfig -p | awk '/libbpf\.so\.1 /{p = $NF} END {print p}')
for b in "$R/usr/local/lib/ghelper/gpu-helper" "$R/bin/dbgload" "$libbpf"; do
    for l in $(ldd "$b" | grep -o '/[^ ]*'); do mkdir -p "$R$(dirname "$l")"; cp -L "$l" "$R$l"; done
done
mkdir -p "$R$(dirname "$libbpf")"
cp -L "$libbpf" "$R$(dirname "$libbpf")/libbpf.so.1"

# DRM driver for the fake GPUs; without it the card node checks are skipped.
i=0
for m in ttm/ttm drm_ttm_helper drm_vram_helper tiny/bochs; do
    f="$MODDIR/kernel/drivers/gpu/drm/$m.ko"
    [[ -f "$f.zst" ]] && zstd -qdc "$f.zst" > "$R/mods/$i.ko"
    [[ -f "$f" ]] && cp "$f" "$R/mods/$i.ko"
    i=$((i + 1))
done

cp "$VM/vmtest.sh" "$R/vmtest.sh"
cat > "$R/init" <<'EOF'
#!/bin/sh
mount -t proc proc /proc
mount -t sysfs sys /sys
mount -t devtmpfs dev /dev
mount -t bpf bpf /sys/fs/bpf
mount -t securityfs none /sys/kernel/security
mount -t tracefs none /sys/kernel/tracing
mount -t tmpfs run /run
for m in /mods/*.ko; do insmod "$m" 2>/dev/null; done
sleep 1
sh /vmtest.sh
poweroff -f
EOF
chmod +x "$R/init"
(cd "$R" && find . | cpio -o -H newc --quiet | gzip -1) > "$W/initrd.gz"

ACCEL="-accel tcg"
[[ -w /dev/kvm ]] && ACCEL="-enable-kvm"

boot() {
    # iGPU: the boot VGA. dGPU: alone behind a root port. dGPU2: behind a bridge it shares.
    # -cpu max: amd64v3 packages (busybox-static on CI) need AVX.
    timeout 600 qemu-system-x86_64 $ACCEL -cpu max -m 1024 -smp 2 -nographic -no-reboot -M q35 \
        -kernel "$KERNEL" -initrd "$W/initrd.gz" -vga std \
        -device pcie-root-port,id=rp1,chassis=1,slot=1 -device secondary-vga,bus=rp1 \
        -device pcie-root-port,id=rp2,chassis=2,slot=2 -device pcie-pci-bridge,id=br1,bus=rp2 \
        -device secondary-vga,bus=br1,addr=1 -device virtio-rng-pci,bus=br1,addr=2 \
        -append "console=ttyS0 quiet panic=-1 lsm=lockdown,capability,landlock,yama,bpf $*" 2>&1 \
        | tr -d '\r' | tee "$W/raw" | sed -n 's/.*VMTEST-BEGIN/VMTEST-BEGIN/; /VMTEST-BEGIN/,/RESULT/p'
}

rc=0
for args in "" "lockdown=integrity"; do
    echo "== kernel $(basename "$KERNEL") ${args:-(no lockdown)}"
    out=$(boot $args) || true
    if [[ "$out" != *VMTEST-BEGIN* && "$ACCEL" != "-accel tcg" ]]; then
        # No test output: show why, then retry without KVM.
        echo "-- no test output with $ACCEL, last qemu lines:"
        tail -n 40 "$W/raw"
        ACCEL="-accel tcg"
        out=$(boot $args) || true
    fi
    echo "$out"
    grep -q 'RESULT pass=[0-9]* fail=0$' <<<"$out" || rc=1
done
exit $rc
