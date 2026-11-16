import { type Accessor, createSignal, onCleanup } from "solid-js";

const MINUTE = 60_000;

const DAY = 60 * 24;

/** The age in its coarsest useful unit ("5m", "2h", "3d"), or null under a minute and past a month. */
function unitAge(thenMs: number, nowMs: number): string | null {
  const minutes = Math.round((nowMs - thenMs) / MINUTE);
  if (minutes < 1 || Math.round(minutes / DAY) >= 30) {
    return null;
  }
  if (minutes < 60) {
    return `${minutes}m`;
  }
  return minutes < DAY ? `${Math.round(minutes / 60)}h` : `${Math.round(minutes / DAY)}d`;
}

const recent = (thenMs: number, nowMs: number): boolean => nowMs - thenMs < DAY * MINUTE;

/** A short "2h ago"-style age of `thenMs` at `nowMs`, falling back to a local date past a month. */
export function compactAge(thenMs: number, nowMs: number): string {
  const age = unitAge(thenMs, nowMs);
  if (age !== null) {
    return `${age} ago`;
  }
  return recent(thenMs, nowMs) ? "just now" : new Date(thenMs).toLocaleDateString();
}

/** A bare "2h"-style age for the end of a row: "now" under a minute, a short local date past a month. */
export function briefAge(thenMs: number, nowMs: number): string {
  const short = (): string =>
    new Date(thenMs).toLocaleDateString(undefined, { month: "short", day: "numeric" });
  return unitAge(thenMs, nowMs) ?? (recent(thenMs, nowMs) ? "now" : short());
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
