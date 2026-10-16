#!/bin/bash
# File Tunnel test lab: the two Android emulators on the Mac, which the tests use as their Android
# clients. `.\mac.ps1 up | halt | status` runs it on the Mac.
#
#   emulators.sh up       make sure both are running and on the LAN: starts them, or refreshes a
#                         pair whose networking went stale, and leaves a healthy pair alone
#   emulators.sh halt     stop both
#   emulators.sh status   are both running and on the LAN?
#
# Both are bridged onto the LAN (-vmnet-bridged, which needs sudo), so each has a real address and
# can be either end of a tunnel. They start one after the other: started together, only one of them
# gets Android's default network. -read-only gives each a throwaway overlay of the one AVD.
# Only these two are touched: other emulators on the Mac (other ports) are left alone.
#
# Inputs (environment, from mac.ps1): FT_AVD, FT_EMU_VERSION and FT_EMU_ARGS (the lab's emulator and
# its extra arguments), FT_PORTS ("5556 5558": client1, then client2), FT_BRIDGE (the Mac's LAN interface).
set -u
: "${FT_AVD:?}" "${FT_EMU_VERSION:?}" "${FT_PORTS:?}" "${FT_BRIDGE:?}"
FT_EMU_ARGS=${FT_EMU_ARGS-}
read -r P1 P2 <<<"$FT_PORTS"
S1="emulator-$P1"
S2="emulator-$P2"
SDK="$HOME/Library/Android/sdk"
ADB="$SDK/platform-tools/adb"
# The lab's own copy of the emulator (setup.sh), not the SDK's, which the Mac's other emulators run.
# It finds the system image through ANDROID_SDK_ROOT.
EMU="$HOME/Library/Android/ft-emulator/$FT_EMU_VERSION/emulator/emulator"
HOSTIP=$(ipconfig getifaddr "$FT_BRIDGE" 2>/dev/null)

# Fully booted: adb sees a device AND Android says boot completed.
booted() {
    [ "$("$ADB" -s "$1" get-state 2>/dev/null)" = device ] &&
    [ "$("$ADB" -s "$1" shell getprop sys.boot_completed 2>/dev/null | tr -dc 0-9)" = 1 ]
}
# The emulator's address on the LAN (its wlan0; eth0 is the emulator's own NAT, 10.0.2.15).
wlanip() { "$ADB" -s "$1" shell ip -4 addr show wlan0 2>/dev/null | grep -oE 'inet [0-9.]+' | awk '{print $2}'; }
lanip() { case "$1" in '' | 10.0.*) return 1 ;; *) return 0 ;; esac; }
# A real ICMP reply is the only trustworthy sign of working networking: an emulator restored from its
# snapshot after the Mac restarted can hold its old address yet pass no traffic. Any of three pings
# will do: after a quiet spell the first can be lost while the address is looked up again (ARP).
reaches() { [ -n "$2" ] && "$ADB" -s "$1" shell "ping -c3 -W2 $2" >/dev/null 2>&1; }
lanreach() { reaches "$1" "$HOSTIP"; }
waitboot() { local i; for i in $(seq 1 90); do booted "$1" && return 0; sleep 4; done; return 1; }
waitip() { local i; for i in $(seq 1 45); do lanip "$(wlanip "$1")" && return 0; sleep 3; done; return 1; }
# Stale networking: switching Wi-Fi off and on makes it associate and take an address afresh.
ensurenet() {
    local i
    for i in 1 2 3 4; do
        lanreach "$1" && return 0
        echo "$1: no LAN - restarting its Wi-Fi ($i)"
        "$ADB" -s "$1" shell 'svc wifi disable; sleep 3; svc wifi enable' >/dev/null 2>&1
        sleep 10
        waitip "$1"
    done
    lanreach "$1"
}

healthy() {
    booted "$S1" && booted "$S2" || return 1
    local ip1 ip2
    ip1=$(wlanip "$S1"); ip2=$(wlanip "$S2")
    lanip "$ip1" && lanip "$ip2" && lanreach "$S1" && lanreach "$S2" && reaches "$S2" "$ip1"
}

stop_both() {
    "$ADB" -s "$S1" emu kill >/dev/null 2>&1
    "$ADB" -s "$S2" emu kill >/dev/null 2>&1
    sleep 2
    # SIGKILL, and the qemu child too: after `emu kill` the child can outlive a plain SIGTERM and keep
    # the console port, and the next start then fails on it, silently.
    for p in "$P1" "$P2"; do
        sudo pkill -9 -f "qemu-system.*-port $p" 2>/dev/null
        sudo pkill -9 -f "emulator.*-port $p" 2>/dev/null
    done
    sleep 3
}

# FT_EMU_ARGS (unquoted: it's a list) is what this emulator version needs on top (settings.yml,
# mac.emulators.emulator.args).
start_one() {
    nohup sudo HOME="$HOME" ANDROID_AVD_HOME="$HOME/.android/avd" ANDROID_SDK_ROOT="$SDK" \
        ANDROID_EMULATOR_HOME="$HOME/.android" "$EMU" -avd "$FT_AVD" \
        -no-window -no-audio -no-boot-anim -read-only -gpu swiftshader_indirect -no-metrics \
        $FT_EMU_ARGS \
        -port "$1" -vmnet-bridged "$FT_BRIDGE" >"$HOME/ft_emulator-$1.log" 2>&1 </dev/null &
}

report() {
    local ok=0 s ip abi
    for s in "$S1" "$S2"; do
        if ! booted "$s"; then echo "$s: not running"; ok=1; continue; fi
        ip=$(wlanip "$s")
        abi=$("$ADB" -s "$s" shell getprop ro.product.cpu.abi 2>/dev/null | tr -d '\r ')
        if lanip "$ip" && lanreach "$s"; then echo "$s: running ($abi), on the LAN at $ip"
        else echo "$s: running ($abi) at '${ip:-no address}', but CAN'T REACH the LAN"; ok=1; fi
    done
    if [ "$ok" = 0 ] && ! reaches "$S2" "$(wlanip "$S1")"; then echo "$S2 can't reach $S1"; ok=1; fi
    return $ok
}

case "${1:-}" in
up)
    [ -x "$EMU" ] || { echo "No emulator $FT_EMU_VERSION at $EMU: run .\\mac.ps1 setup first" >&2; exit 1; }
    # Restarting Wi-Fi refreshes a bridged emulator's stale networking; it can't bridge one that started
    # on the emulator's own network (10.0.2.x), so that pair is restarted instead.
    if booted "$S1" && booted "$S2" && lanip "$(wlanip "$S1")" && lanip "$(wlanip "$S2")"; then
        ensurenet "$S1"; ensurenet "$S2"
    fi
    if healthy; then
        echo "Both emulators were already up and on the LAN."
    else
        echo "Starting $S1 and $S2 ..."
        stop_both
        # adb drops the stopped pair by itself. Its server is shared with the Mac's other emulators, so it
        # isn't restarted: that would cut their adb sessions too.
        for i in $(seq 1 10); do "$ADB" devices | grep -qE "^($S1|$S2)[[:space:]]" || break; sleep 1; done
        start_one "$P1"; waitboot "$S1"; waitip "$S1"; ensurenet "$S1"
        start_one "$P2"; waitboot "$S2"; waitip "$S2"; ensurenet "$S2"
    fi
    # Root adbd (the images are userdebug): the sshfs rows need root for the FUSE mount.
    for s in "$S1" "$S2"; do
        booted "$s" && { "$ADB" -s "$s" root >/dev/null 2>&1; "$ADB" -s "$s" wait-for-device; }
    done
    # -vmnet-bridged bridges each emulator's eth0 (its mobile-data link, 10.0.2.15) onto the LAN as well, and
    # both emulators' eth0 have QEMU's default MAC, 52:54:00:12:34:56. Linux answers ARP for any of its
    # addresses on any interface, so eth0 answered for the Wi-Fi address too, and the LAN sometimes sent one
    # emulator's traffic to the MAC both share. Answer ARP only on the interface that has the address, and
    # announce from it. (Lost at every restart, so set on every `up`.)
    for s in "$S1" "$S2"; do
        booted "$s" && "$ADB" -s "$s" shell 'echo 1 > /proc/sys/net/ipv4/conf/all/arp_ignore; echo 2 > /proc/sys/net/ipv4/conf/all/arp_announce'
    done
    report
    ;;
halt)
    stop_both
    echo "Stopped $S1 and $S2."
    ;;
status)
    report
    ;;
*)
    echo "usage: emulators.sh up | halt | status" >&2
    exit 2
    ;;
esac
