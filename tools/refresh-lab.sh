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
HZ="${1:-auto}"
HZARG=""
[ "$HZ" != "auto" ] && HZARG="VBLANK_SHIM_HZ=$HZ"
SHIM=/tmp/vblank-shim.so
cc -O2 -Wall -fPIC -shared -o "$SHIM" tools/vblank-shim.c || exit 1
# NVIDIA 560+ ships egl-wayland2 with correct explicit sync; forcing implicit there degrades performance
# and is suspected of the 60Hz fence stall itself. Set REFRESH_LAB_KEEP_EXPLICIT_SYNC=1 to leave it on.
if [ -z "${REFRESH_LAB_KEEP_EXPLICIT_SYNC:-}" ]; then
	export __NV_DISABLE_EXPLICIT_SYNC=1
else
	echo "explicit sync left ENABLED (no __NV_DISABLE_EXPLICIT_SYNC)"
fi
STARTED="$(date '+%Y-%m-%d %H:%M:%S')"

echo "== environment"
echo "  kernel: $(uname -r)   desktop: ${XDG_CURRENT_DESKTOP:-?} (${XDG_SESSION_TYPE:-?})"
command -v nvidia-smi >/dev/null 2>&1 && echo "  nvidia: $(nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>/dev/null | head -1)   nvidia_drm modeset: $(cat /sys/module/nvidia_drm/parameters/modeset 2>/dev/null)"
{ pacman -Q webkit2gtk-4.1 2>/dev/null || dpkg -l 2>/dev/null | awk '/libwebkit2gtk-4.1/{print $2" "$3}'; } | sed 's/^/  webkit: /'
command -v kwin_wayland >/dev/null 2>&1 && echo "  kwin: $(kwin_wayland --version 2>/dev/null | head -1)"

# THROTTLE_FPS=7 never divides a real rate, so WebKit rejects it and prints the rate it believes —
# making every armed run self-report its nominal on stderr.
names=(baseline trace fix fix-syncpatch fix-shm)
arms=(
	""
	"LD_PRELOAD=$SHIM"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_STEER=1 VBLANK_SHIM_FIX=1 $HZARG WEBKIT_DISPLAY_REFRESH_THROTTLE_FPS=7"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_STEER=1 VBLANK_SHIM_FIX=1 VBLANK_SHIM_SYNCPATCH=1 $HZARG WEBKIT_DISPLAY_REFRESH_THROTTLE_FPS=7"
	"LD_PRELOAD=$SHIM VBLANK_SHIM_STEER=1 VBLANK_SHIM_FIX=1 $HZARG WEBKIT_DISPLAY_REFRESH_THROTTLE_FPS=7 WEBKIT_DISABLE_DMABUF_RENDERER=1"
)


# The web process resolves GL through private dlsym handles no interpose can reach, so instead of guessing
# APIs, ask the kernel: wchan names the kernel symbol each thread is blocked in, per thread, mid-frame.
sample_blocks() {
	echo "  -- mid-run thread blocks (kernel wchan, 12 samples)"
	{
		for pass in $(seq 1 12); do
			for pid in $(pgrep -x webkit-fps) $(pgrep -x WebKitWebProces); do
				proc="$(cat "/proc/$pid/comm" 2>/dev/null)"
				for task in /proc/"$pid"/task/*; do
					wchan="$(cat "$task/wchan" 2>/dev/null)"
					thread="$(cat "$task/comm" 2>/dev/null)"
					[ -n "$wchan" ] && [ "$wchan" != "0" ] && echo "$proc/$thread blocked_in=$wchan"
				done
			done
			sleep 0.15
		done
	} | sort | uniq -c | sort -rn | head -18 | sed 's/^/  /'
	if sudo -n true 2>/dev/null; then
		echo "  -- kernel stacks mentioning fence/sync (needs root)"
		for pid in $(pgrep -x webkit-fps) $(pgrep -x WebKitWebProces); do
			for task in /proc/"$pid"/task/*; do
				stack="$(sudo -n cat "$task/stack" 2>/dev/null)"
				if echo "$stack" | grep -qiE "fence|sync_file"; then
					echo "  == $(cat "$task/comm" 2>/dev/null) ($task)"
					echo "$stack" | head -10 | sed 's/^/    /'
				fi
			done
		done
	fi
}

for i in "${!names[@]}"; do
	log="/tmp/refresh-lab-${names[$i]}.log"
	echo "== ${names[$i]}"
	# shellcheck disable=SC2086
	if [ "${names[$i]}" = "fix" ]; then
		env ${arms[$i]} timeout 90 dotnet run tools/webkit-fps.cs >"$log" 2>&1 &
		RUN=$!
		tries=0
		until pgrep -x webkit-fps >/dev/null 2>&1 || [ "$tries" -ge 120 ]; do
			sleep 0.25
			tries=$((tries + 1))
		done
		sleep 1.5
		sample_blocks
		wait "$RUN"
	else
		env ${arms[$i]} timeout 90 dotnet run tools/webkit-fps.cs >"$log" 2>&1
	fi
	grep -E "^FPS" "$log" || echo "  (no FPS line — see $log)"
	grep -m1 "rejected" "$log" | sed 's/^/  /'
	grep -m8 -E "vblank-shim.*(syncpatch|steer|active|drmWaitVBlank #0|refresh_rate|emulation)" "$log" | sed 's/^/  /'
	grep -m10 -E "point [0-9]+ signaled" "$log" | sed 's/^/  /'
	grep -m4 "summary:" "$log" | sed 's/^/  /'
	grep -m3 "hist " "$log" | sed 's/^/  /'
done

echo
echo "== journal (vblank monitor faults since $STARTED; best effort)"
JOURNAL="$(journalctl --since "$STARTED" --no-pager 2>/dev/null | grep -iaE 'vblank' | tail -8)"
[ -n "$JOURNAL" ] && echo "$JOURNAL" | sed 's/^/  /' \
	|| echo "  (nothing readable — the silent !displayID timer path logs no fault at all)"

echo
echo "== summary (panel target: ${HZ})"
for n in "${names[@]}"; do
	printf "  %-14s %s\n" "$n" "$(grep -m1 '^FPS' "/tmp/refresh-lab-$n.log" 2>/dev/null || echo '—')"
done
echo "  full logs: /tmp/refresh-lab-*.log"
echo
echo "How to read it: 'trace' now walks the whole DRM discovery (devices -> resources -> connector ->"
echo "encoder), so the last line before it stops names the failing step. 'fix' should self-report"
echo "the panel's rate in its rejected-line if the monitor constructed; ~10x that in frames means won."
echo "'fix-shm' isolates DMA-BUF presentation: full rate there but 60 in 'fix' = present-path clamp."
echo "Keep the probe window visible and unoccluded for the whole run — occluded Wayland windows are throttled."
