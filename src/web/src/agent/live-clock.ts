import { type Accessor, createEffect, createSignal, onCleanup } from "solid-js";

// Every running clock ticks at one instant, so two readings of one duration never show different seconds.
const clocks = new Set<(now: number) => void>();
let timer: ReturnType<typeof setInterval> | undefined;
const tick = (): void => {
  const now = Date.now();
  for (const set of clocks) set(now);
};

/** The current time, ticking each second while `running` holds. */
export function liveNow(running: Accessor<boolean>): Accessor<number> {
  const [now, setNow] = createSignal(Date.now());
  createEffect(() => {
    if (!running()) return;
    clocks.add(setNow);
    tick();
    timer ??= setInterval(tick, 1000);
    onCleanup(() => {
      clocks.delete(setNow);
      if (clocks.size > 0) return;
      clearInterval(timer);
      timer = undefined;
    });
  });
  return now;
}
