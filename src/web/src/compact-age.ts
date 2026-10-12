import { type Accessor, createSignal, onCleanup } from "solid-js";

const MINUTE = 60_000;

/** A short "2h ago"-style age of `thenMs` at `nowMs`, falling back to a local date past a month. */
export function compactAge(thenMs: number, nowMs: number): string {
  const minutes = Math.round((nowMs - thenMs) / MINUTE);
  if (minutes < 1) {
    return "just now";
  }
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  if (minutes < 60 * 24) {
    return `${Math.round(minutes / 60)}h ago`;
  }
  const days = Math.round(minutes / (60 * 24));
  return days < 30 ? `${days}d ago` : new Date(thenMs).toLocaleDateString();
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
