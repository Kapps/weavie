import { createSignal, onCleanup } from "solid-js";

export const COMPACT_MEDIA = "(max-width: 760px), ((max-height: 520px) and (pointer: coarse))";

/** Tracks the compact shell breakpoint without tying mobile behavior to a user-agent. */
export function useCompactMode(): () => boolean {
  const query = window.matchMedia(COMPACT_MEDIA);
  const [compact, setCompact] = createSignal(query.matches);
  const update = (event: MediaQueryListEvent): void => {
    setCompact(event.matches);
  };
  query.addEventListener("change", update);
  onCleanup(() => query.removeEventListener("change", update));
  return compact;
}
