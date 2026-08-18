#!/usr/bin/env bash
# One-shot lab for WebKitGTK's 60Hz cap: builds tools/vblank-shim.c, then sweeps trace + workaround arms
# over tools/webkit-fps.cs and prints a summary. Usage: tools/refresh-lab.sh [panel-hz]   (default 240)
#
# Why the arms look like this: rendering updates follow the nominal rate WebKit *believes*, not the tick
# cadence — pacing the fallback timer's sleeps at 240Hz measurably leaves rAF at 60 (the timer's nominal is
# hardcoded 60). Only the DRM vblank monitor reads the real rate from GDK, so the workaround arms make that
# path succeed (emulating the vblank wait if the driver refuses it) instead of speeding the timer.
set -u
cd "$(dirname "$0")/.."
HZ="${1:-240}"
SHIM=/tmp/vblank-shim.so
cc -O2 -Wall -fPIC -shared -o "$SHIM" tools/vblank-shim.c || exit 1
export __NV_DISABLE_EXPLICIT_SYNC=1

names=(baseline believed-rate trace fix-drm fix-drm-shm fix-drm-swap0 timer-control)
arms=(
	""
	"WEBKIT_DISPLAY_REFRESH_THROTTLE_FPS=7"
	"LD_PRELOAD=$SHIM"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_FIX=1 VBLANK_SHIM_HZ=$HZ"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_FIX=1 VBLANK_SHIM_HZ=$HZ WEBKIT_DISABLE_DMABUF_RENDERER=1"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_FIX=1 VBLANK_SHIM_HZ=$HZ VBLANK_SHIM_SWAP0=1"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_TIMERFIX=1 VBLANK_SHIM_HZ=$HZ WEBKIT_FORCE_VBLANK_TIMER=1"
)

for i in "${!names[@]}"; do
	log="/tmp/refresh-lab-${names[$i]}.log"
	echo "== ${names[$i]}"
	# shellcheck disable=SC2086
	env ${arms[$i]} timeout 90 dotnet run tools/webkit-fps.cs >"$log" 2>&1
	grep -E "^FPS" "$log" || echo "  (no FPS line — see $log)"
	grep -m1 "rejected" "$log" | sed 's/^/  /'
	grep -m8 "vblank-shim" "$log" | sed 's/^/  /'
done

echo
echo "== summary (panel target: ${HZ}Hz)"
for n in "${names[@]}"; do
	printf "  %-16s %s\n" "$n" "$(grep -m1 '^FPS' "/tmp/refresh-lab-$n.log" 2>/dev/null || echo '—')"
done
echo "  full logs: /tmp/refresh-lab-*.log"
echo
echo "How to read it: 'believed-rate' prints the fps WebKit thinks the display runs at. 'trace' shows"
echo "whether drmWaitVBlank is reached (and its errno) or only 16ms timer sleeps appear. If fix-drm-shm"
echo "reaches ~${HZ} but fix-drm stays at 60, the vblank fix works and the remaining clamp is DMA-BUF"
echo "presentation; timer-control staying at 60 despite 240Hz ticks demonstrates the nominal-rate gate."
