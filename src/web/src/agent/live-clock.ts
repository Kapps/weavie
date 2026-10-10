import { type Accessor, createEffect, createSignal, onCleanup } from "solid-js";

/** The current time, ticking each second while `running` holds. */
export function liveNow(running: Accessor<boolean>): Accessor<number> {
  const [now, setNow] = createSignal(Date.now());
  createEffect(() => {
    if (!running()) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    onCleanup(() => clearInterval(timer));
  });
  return now;
}
