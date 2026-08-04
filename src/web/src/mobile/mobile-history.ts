import { type Accessor, createEffect, createMemo, createSignal, onCleanup } from "solid-js";
import type { MobileSurface } from "./MobileSurfaceBar";

const STATE_KEY = "__weavieMobileNavigation";
const ROOT_STACK: readonly MobileSurface[] = ["inbox"];

interface NavigationState {
  hasForward: boolean;
  stack: readonly MobileSurface[];
}

export interface MobileHistory {
  back: () => void;
  backTarget: Accessor<MobileSurface | null>;
  drill: (surface: MobileSurface) => void;
  select: (surface: MobileSurface) => void;
  surface: Accessor<MobileSurface>;
}

/** Browser-backed mobile routes: peer surfaces replace the top entry while drill-ins push one. */
export function createMobileHistory(compact: Accessor<boolean>): MobileHistory {
  const [stack, setStack] = createSignal<readonly MobileSurface[]>(ROOT_STACK);
  const surface = createMemo<MobileSurface>(() => stack().at(-1) ?? "inbox");
  const backTarget = createMemo<MobileSurface | null>(() => stack().at(-2) ?? null);

  const currentNavigation = (): NavigationState => {
    const current = readNavigation(history.state);
    if (current !== null) {
      return current;
    }
    const root = { hasForward: false, stack: ROOT_STACK };
    history.replaceState(withNavigation(history.state, root), "");
    return root;
  };
  const push = (current: readonly MobileSurface[], next: readonly MobileSurface[]): void => {
    history.replaceState(withNavigation(history.state, { hasForward: true, stack: current }), "");
    history.pushState(withNavigation(history.state, { hasForward: false, stack: next }), "");
  };
  const restoreCurrentStack = (): void => {
    if (!compact()) {
      return;
    }
    setStack(currentNavigation().stack);
  };
  window.addEventListener("popstate", restoreCurrentStack);
  onCleanup(() => window.removeEventListener("popstate", restoreCurrentStack));

  createEffect(() => {
    restoreCurrentStack();
  });

  const back = (): void => {
    if (!compact()) {
      return;
    }
    const current = currentNavigation().stack;
    if (current.length === 1) {
      return;
    }
    setStack(current.slice(0, -1));
    history.back();
  };

  const select = (next: MobileSurface): void => {
    if (!compact()) {
      setStack([next]);
      return;
    }

    const navigation = currentNavigation();
    const current = navigation.stack;
    const active = current.at(-1);
    if (next === active) {
      return;
    }
    if (next === "inbox") {
      setStack(ROOT_STACK);
      history.go(-(current.length - 1));
      return;
    }
    if (next === current.at(-2)) {
      back();
      return;
    }
    const nextStack =
      active === "inbox" || navigation.hasForward
        ? [...current, next]
        : [...current.slice(0, -1), next];
    if (active === "inbox" || navigation.hasForward) {
      push(current, nextStack);
    } else {
      history.replaceState(
        withNavigation(history.state, { hasForward: false, stack: nextStack }),
        "",
      );
    }
    setStack(nextStack);
  };

  const drill = (next: MobileSurface): void => {
    if (!compact()) {
      setStack([next]);
      return;
    }
    if (next === "inbox") {
      select(next);
      return;
    }
    const current = currentNavigation().stack;
    if (next === current.at(-1)) {
      return;
    }
    const nextStack = [...current, next];
    push(current, nextStack);
    setStack(nextStack);
  };

  return { back, backTarget, drill, select, surface };
}

function readNavigation(state: unknown): NavigationState | null {
  if (state === null || typeof state !== "object") {
    return null;
  }
  const navigation = (state as Record<string, unknown>)[STATE_KEY];
  if (navigation === null || typeof navigation !== "object") {
    return null;
  }
  const { hasForward, stack } = navigation as Record<string, unknown>;
  if (
    typeof hasForward !== "boolean" ||
    !Array.isArray(stack) ||
    stack.length === 0 ||
    stack[0] !== "inbox" ||
    !stack.every(isMobileSurface) ||
    stack.some((surface, index) => index > 0 && surface === stack[index - 1])
  ) {
    return null;
  }
  return { hasForward, stack };
}

function isMobileSurface(value: unknown): value is MobileSurface {
  return (
    value === "inbox" ||
    value === "terminal:claude" ||
    value === "terminal:shell" ||
    value === "editor"
  );
}

function withNavigation(state: unknown, navigation: NavigationState): Record<string, unknown> {
  const current = state !== null && typeof state === "object" ? state : {};
  return { ...current, [STATE_KEY]: navigation };
}
