#!/usr/bin/env bash
# One-shot lab for WebKitGTK's 60Hz cap: builds tools/vblank-shim.c, then sweeps trace + workaround arms
# over tools/webkit-fps.cs and prints a summary. Usage: tools/refresh-lab.sh [panel-hz]   (default 240)
#
# Why the arms look like this: rendering updates follow the nominal rate WebKit *believes*, not the tick
# cadence — pacing the fallback timer's sleeps at 240Hz measurably leaves rAF at 60 (the timer's nominal is
# hardcoded 60). Only the DRM vblank monitor reads the real rate from GDK, so the fix arms make that path
# construct: steer fills in encoder/crtc ids drivers hide from non-master clients, and the emulator answers
# drmWaitVBlank on a precise grid when the driver refuses the ioctl.
set -u
cd "$(dirname "$0")/.."
HZ="${1:-240}"
SHIM=/tmp/vblank-shim.so
cc -O2 -Wall -fPIC -shared -o "$SHIM" tools/vblank-shim.c || exit 1
export __NV_DISABLE_EXPLICIT_SYNC=1
STARTED="$(date '+%Y-%m-%d %H:%M:%S')"

# THROTTLE_FPS=7 never divides a real rate, so WebKit rejects it and prints the rate it believes —
# making every armed run self-report its nominal on stderr.
names=(baseline believed-rate trace fix fix-shm)
arms=(
	""
	"WEBKIT_DISPLAY_REFRESH_THROTTLE_FPS=7"
	"LD_PRELOAD=$SHIM"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_STEER=1 VBLANK_SHIM_FIX=1 VBLANK_SHIM_HZ=$HZ WEBKIT_DISPLAY_REFRESH_THROTTLE_FPS=7"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_STEER=1 VBLANK_SHIM_FIX=1 VBLANK_SHIM_HZ=$HZ WEBKIT_DISPLAY_REFRESH_THROTTLE_FPS=7 WEBKIT_DISABLE_DMABUF_RENDERER=1"
)

for i in "${!names[@]}"; do
	log="/tmp/refresh-lab-${names[$i]}.log"
	echo "== ${names[$i]}"
	# shellcheck disable=SC2086
	env ${arms[$i]} timeout 90 dotnet run tools/webkit-fps.cs >"$log" 2>&1
	grep -E "^FPS" "$log" || echo "  (no FPS line — see $log)"
	grep -m1 "rejected" "$log" | sed 's/^/  /'
	grep -m20 "vblank-shim" "$log" | sed 's/^/  /'
done

echo
echo "== journal (vblank monitor faults since $STARTED; best effort)"
JOURNAL="$(journalctl --since "$STARTED" --no-pager 2>/dev/null | grep -iaE 'vblank' | tail -8)"
[ -n "$JOURNAL" ] && echo "$JOURNAL" | sed 's/^/  /' \
	|| echo "  (nothing readable — the silent !displayID timer path logs no fault at all)"

echo
echo "== summary (panel target: ${HZ}Hz)"
for n in "${names[@]}"; do
	printf "  %-14s %s\n" "$n" "$(grep -m1 '^FPS' "/tmp/refresh-lab-$n.log" 2>/dev/null || echo '—')"
done
echo "  full logs: /tmp/refresh-lab-*.log"
echo
echo "How to read it: 'trace' now walks the whole DRM discovery (devices -> resources -> connector ->"
echo "encoder), so the last line before it stops names the failing step. 'fix' should self-report"
echo "'refresh rate ${HZ}fps' in its rejected-line if the monitor constructed; ~$((HZ * 10)) frames means won."
echo "'fix-shm' isolates DMA-BUF presentation: ~${HZ}0 frames there but 60 in 'fix' = present-path clamp."
