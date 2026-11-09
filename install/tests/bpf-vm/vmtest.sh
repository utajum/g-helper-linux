#!/bin/sh
# Runs inside the VM as root. "probe" and "launcher" are one static binary under two names;
# neither name is allowed, so their results show what the filter does to ordinary apps.
echo VMTEST-BEGIN
H=/usr/local/lib/ghelper/gpu-helper
P=0; F=0
pass() { echo "PASS $1"; P=$((P+1)); }
fail() { echo "FAIL $1"; F=$((F+1)); }
ok() { if sh -c "$2" >/dev/null 2>&1; then pass "$1"; else fail "$1"; fi; }
no() { if sh -c "$2" >/dev/null 2>&1; then fail "$1"; else pass "$1"; fi; }
rc() { if [ "$2" = "$3" ]; then pass "$1"; else fail "$1 (rc=$2)"; fi; }
verifier() { echo "--- verifier: $*"; dbgload /f.o "$@" 2>&1 | tail -15; }

for d in /sys/bus/pci/devices/*; do
    b=${d##*/}
    c=$(cat $d/class)
    [ "$c" = 0x030000 ] && [ "$(cat $d/boot_vga 2>/dev/null)" = 1 ] && IG=$b
    if [ "$c" = 0x038000 ]; then [ -z "$DG" ] && DG=$b || DG2=$b; fi
done
IC=$(ls /sys/bus/pci/devices/$IG/drm 2>/dev/null | grep '^card' | head -1)
DC=$(ls /sys/bus/pci/devices/$DG/drm 2>/dev/null | grep '^card' | head -1)
DCONN=$(ls /sys/class/drm | grep "^$DC-" | head -1)
RP=$(basename "$(dirname "$(readlink -f /sys/bus/pci/devices/$DG)")")
LOCK=$(sed -n 's/.*\[\(.*\)\].*/\1/p' /sys/kernel/security/lockdown)
echo "igpu=$IG/$IC dgpu=$DG/$DC conn=$DCONN parent=$RP dgpu2=$DG2 lockdown=$LOCK"
ADDR=$(echo $DG | tr ':.' '__')
V=/sys/bus/pci/devices/$DG/vendor

$H bpf-block on $DG >/dev/null
r=$?
rc "on" $r 0
[ $r = 0 ] || verifier gh_file_open gh_inode_permission gh_inode_getattr
LISTING=0; [ -e /sys/fs/bpf/ghelper/link_gh_getdents_exit ] && LISTING=1
PROC=0; [ -e /sys/fs/bpf/ghelper/link_gh_exec ] && PROC=1
rc "per-process hooks attached" $PROC 1
[ $PROC = 1 ] || verifier gh_exec gh_fork gh_exit
if [ "$LOCK" = none ]; then
    rc "listing hooks attached" $LISTING 1
    [ $LISTING = 1 ] || verifier gh_getdents_enter gh_getdents_exit
else
    rc "lockdown: listing hooks dropped" $LISTING 0
fi

no "dgpu sysfs denied" "probe open $V"
ok "igpu sysfs open" "probe open /sys/bus/pci/devices/$IG/vendor"
ok "allowed comm passes" "$H bpf-caps"
case "$RP" in pci*) ;; *) no "lone parent bridge denied" "probe open /sys/bus/pci/devices/$RP/vendor" ;; esac
if [ -n "$IC" ] && [ -n "$DC" ]; then
    no "dgpu card denied" "probe open /dev/dri/$DC"
    ok "igpu card open" "probe open /dev/dri/$IC"
    [ -n "$DCONN" ] && no "connector denied" "probe open /sys/class/drm/$DCONN/status"
fi
if [ $LISTING = 1 ]; then
    no "dgpu hidden in pci listing" "probe list /sys/bus/pci/devices $DG"
    case "$RP" in pci*) ;; *) no "lone parent bridge hidden" "probe list /sys/bus/pci/devices $RP" ;; esac
    if [ -n "$DC" ]; then
        no "dgpu hidden in /dev/dri" "probe list /dev/dri $DC"
        no "drm class link hidden" "probe list /sys/class/drm $DC"
        ok "igpu listed" "probe list /dev/dri $IC"
    fi
    [ -n "$DCONN" ] && no "connector link hidden" "probe list /sys/class/drm $DCONN"
fi

if [ $PROC = 1 ]; then
    ok "env GHELPER_DGPU" "GHELPER_DGPU=1 probe open $V"
    ok "env prime offload" "__NV_PRIME_RENDER_OFFLOAD=1 probe open $V"
    ok "env DRI_PRIME=1" "DRI_PRIME=1 probe open $V"
    ok "env DRI_PRIME by address" "DRI_PRIME=pci-$ADDR probe open $V"
    ok "env DRI_PRIME by ids" "DRI_PRIME=1234:1111 probe open $V"
    no "env DRI_PRIME=0" "DRI_PRIME=0 probe open $V"
    ok "env cardwire alias" "CARDWIRE_ALLOW=1 probe open $V"
    ok "env after long var" "BIG=$(printf '%3000s' | tr ' ' x) GHELPER_DGPU=1 probe open $V"
    ok "child of env proc" "GHELPER_DGPU=1 probe spawn /bin/probe open $V"
    no "child of plain proc" "probe spawn /bin/probe open $V"
    ok "force: dgpu allowed" "GHELPER_FORCE_DGPU=1 probe open $V"
    [ $LISTING = 1 ] && ok "env proc sees listing" "GHELPER_DGPU=1 probe list /sys/bus/pci/devices $DG"
    if [ -n "$IC" ]; then
        no "force: igpu card denied" "GHELPER_FORCE_DGPU=1 probe open /dev/dri/$IC"
        no "force child: igpu denied" "CARDWIRE_FORCE_DGPU=1 probe spawn /bin/probe open /dev/dri/$IC"
        ok "allow: igpu card open" "GHELPER_DGPU=1 probe open /dev/dri/$IC"
    fi

    $H bpf-block allow-comm SteamAppId=570 >/dev/null
    ok "steam id allowed" "SteamAppId=570 probe open $V"
    no "other steam id" "SteamAppId=571 probe open $V"
    $H bpf-block deny-comm SteamAppId=570 >/dev/null
    no "steam id removed" "SteamAppId=570 probe open $V"

    launcher delay 3 spawn /bin/probe open $V &
    LP=$!
    sleep 1
    $H bpf-block allow-comm launcher >/dev/null
    ok "pick by name" "launcher open $V"
    wait $LP; rc "seeded running pick passes to child" $? 0
    ok "pick passes to child" "launcher spawn /bin/probe open $V"
    $H bpf-block deny-comm launcher >/dev/null
    no "pick removed" "launcher open $V"
    no "pick child removed" "launcher spawn /bin/probe open $V"

    probe delay 2 open $V &
    AP=$!
    $H bpf-block allow-pid $AP >/dev/null
    wait $AP; rc "allow-pid" $? 0
    if [ -n "$IC" ]; then
        probe delay 2 open /dev/dri/$IC 2>/dev/null &
        FP=$!
        $H bpf-block allow-pid $FP force >/dev/null
        wait $FP; rc "allow-pid force hides igpu" $? 1
    fi
fi
no "allow-pid bad arg" "$H bpf-block allow-pid 1x"
ok "refused lists probe" "$H bpf-block refused | grep -q 'probe$'"

$H bpf-block off >/dev/null
ok "off: dgpu visible" "probe open $V"
[ -n "$DC" ] && ok "off: card visible" "probe open /dev/dri/$DC"
ok "off: pins gone" "! test -e /sys/fs/bpf/ghelper"

umount /sys/fs/bpf
$H bpf-block on $DG >/dev/null
rc "on: mounts a missing bpffs" $? 0
ok "bpffs mounted again" "grep -q ' /sys/fs/bpf bpf ' /proc/mounts"
$H bpf-block off >/dev/null

if [ -n "$DG2" ]; then
    BR=$(basename "$(dirname "$(readlink -f /sys/bus/pci/devices/$DG2)")")
    $H bpf-block on $DG2 >/dev/null
    no "shared bridge: dgpu2 denied" "probe open /sys/bus/pci/devices/$DG2/vendor"
    ok "shared bridge stays visible" "probe open /sys/bus/pci/devices/$BR/vendor"
    $H bpf-block off >/dev/null
fi
echo "RESULT pass=$P fail=$F"
