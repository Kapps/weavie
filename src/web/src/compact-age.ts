import { type Accessor, createSignal, onCleanup } from "solid-js";

const MINUTE = 60_000;

const DAY = 60 * 24;

const minutesSince = (thenMs: number, nowMs: number): number =>
  Math.round((nowMs - thenMs) / MINUTE);

/** `minutes` (at least one) in its coarsest useful unit ("5m", "2h", "3d"), or null past a month. */
function unitAge(minutes: number): string | null {
  if (minutes < 60) {
    return `${minutes}m`;
  }
  if (minutes < DAY) {
    return `${Math.round(minutes / 60)}h`;
  }
  const days = Math.round(minutes / DAY);
  return days < 30 ? `${days}d` : null;
}

/** A short "2h ago"-style age of `thenMs` at `nowMs`, falling back to a local date past a month. */
export function compactAge(thenMs: number, nowMs: number): string {
  const minutes = minutesSince(thenMs, nowMs);
  if (minutes < 1) {
    return "just now";
  }
  const age = unitAge(minutes);
  return age === null ? new Date(thenMs).toLocaleDateString() : `${age} ago`;
}

/** A bare "2h"-style age for the end of a row: "now" under a minute, a short local date past a month. */
export function briefAge(thenMs: number, nowMs: number): string {
  const minutes = minutesSince(thenMs, nowMs);
  return minutes < 1
    ? "now"
    : (unitAge(minutes) ??
        new Date(thenMs).toLocaleDateString(undefined, { month: "short", day: "numeric" }));
}

const [now, setNow] = createSignal(Date.now());
let watchers = 0;
let timer: ReturnType<typeof setInterval> | undefined;

/** The current time (ms), ticking each half minute while any reactive owner that asked for it is alive. */
export function useNow(): Accessor<number> {
  if (watchers++ === 0) {
    setNow(Date.now());
    timer = setInterval(() => setNow(Date.now()), MINUTE / 2);
  }
  onCleanup(() => {
    if (--watchers === 0) {
      clearInterval(timer);
    }
  });
  return now;
}
