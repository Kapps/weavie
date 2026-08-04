import { type Accessor, createEffect, createSignal, onCleanup } from "solid-js";
import type { MobileSurface } from "./MobileSurfaceBar";

const STATE_KEY = "__weavieMobileSurface";

export interface MobileHistory {
  surface: Accessor<MobileSurface>;
  navigate: (surface: MobileSurface) => void;
}

/** Gives the compact inbox/detail transition one browser-history entry without stacking pane-tab changes. */
export function createMobileHistory(compact: Accessor<boolean>): MobileHistory {
  const [surface, setSurface] = createSignal<MobileSurface>("inbox");

  const onPopState = (event: PopStateEvent): void => {
    if (!compact()) {
      return;
    }
    setSurface(readSurface(event.state) ?? "inbox");
  };
  window.addEventListener("popstate", onPopState);
  onCleanup(() => window.removeEventListener("popstate", onPopState));

  createEffect(() => {
    if (!compact()) {
      return;
    }
    const restored = readSurface(history.state);
    if (restored === null) {
      history.replaceState(withSurface(history.state, "inbox"), "");
      setSurface("inbox");
    } else {
      setSurface(restored);
    }
  });

  const navigate = (next: MobileSurface): void => {
    if (!compact()) {
      setSurface(next);
      return;
    }

    const current = readSurface(history.state);
    if (next === "inbox" && current !== null && current !== "inbox") {
      setSurface("inbox");
      history.back();
      return;
    }

    if (current === null) {
      history.replaceState(withSurface(history.state, "inbox"), "");
    }
    if (next !== "inbox" && (current === null || current === "inbox")) {
      history.pushState(withSurface(history.state, next), "");
    } else {
      history.replaceState(withSurface(history.state, next), "");
    }
    setSurface(next);
  };

  return { surface, navigate };
}

function readSurface(state: unknown): MobileSurface | null {
  if (state === null || typeof state !== "object") {
    return null;
  }
  const value = (state as Record<string, unknown>)[STATE_KEY];
  return value === "inbox" ||
    value === "terminal:claude" ||
    value === "terminal:shell" ||
    value === "editor"
    ? value
    : null;
}

function withSurface(state: unknown, surface: MobileSurface): Record<string, unknown> {
  const current = state !== null && typeof state === "object" ? state : {};
  return { ...current, [STATE_KEY]: surface };
}
