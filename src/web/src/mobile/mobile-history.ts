import { type Accessor, createEffect, createMemo, createSignal, onCleanup } from "solid-js";
import type { MobileSurface } from "./MobileSurfaceBar";

const STATE_KEY = "__weavieMobileNavigation";
const ROOT_STACK: readonly MobileSurface[] = ["inbox"];

interface NavigationState {
  hasForward: boolean;
  // A drill-in entry pushed inside the user's gesture before its async destination is known.
  reservation: number | null;
  stack: readonly MobileSurface[];
}

export interface MobileHistory {
  backTarget: Accessor<MobileSurface | null>;
  drill: (surface: MobileSurface) => void;
  preview: (surface: MobileSurface) => MobilePreview | null;
  reserve: () => () => void;
  select: (surface: MobileSurface) => void;
  surface: Accessor<MobileSurface>;
}

/** A gesture's navigation whose history entry has moved while the surface it left still renders. */
export interface MobilePreview {
  cancel: () => void;
  commit: () => void;
  /** The browser navigated on its own, so this preview can neither commit nor cancel. */
  dropped: Accessor<boolean>;
}

interface Move {
  apply: () => void;
  stack: readonly MobileSurface[];
  undo: () => void;
}

interface ActivePreview {
  drop: () => void;
  move: Move;
  target: MobileSurface;
}

// One history write; a traversal returns the depth it must land on before the next write may run.
type HistoryOp = () => number | null;

/** Browser-backed mobile routes: peer surfaces replace the top entry while drill-ins push one. */
export function createMobileHistory(compact: Accessor<boolean>): MobileHistory {
  const [stack, setStack] = createSignal<readonly MobileSurface[]>(ROOT_STACK);
  const surface = createMemo<MobileSurface>(() => stack().at(-1) ?? "inbox");
  const backTarget = createMemo<MobileSurface | null>(() => stack().at(-2) ?? null);
  let nextReservation = 0;
  let active: ActivePreview | null = null;

  const currentNavigation = (): NavigationState => {
    const current = readNavigation(history.state);
    if (current !== null) {
      return current;
    }
    const root = { hasForward: false, reservation: null, stack: ROOT_STACK };
    history.replaceState(withNavigation(history.state, root), "");
    return root;
  };
  // Traversals land asynchronously, so writes queue behind them and moves plan from the entry the queue
  // leaves current rather than from history.state.
  let intended = currentNavigation();
  const queue: HistoryOp[] = [];
  let awaitedDepth: number | null = null;
  const run = (...ops: HistoryOp[]): void => {
    queue.push(...ops);
    while (awaitedDepth === null && queue.length > 0) {
      awaitedDepth = queue.shift()!();
    }
  };
  const write =
    (navigation: NavigationState): HistoryOp =>
    () => {
      history.replaceState(withNavigation(history.state, navigation), "");
      return null;
    };
  const pushEntry: HistoryOp = () => {
    history.pushState(history.state, "");
    return null;
  };
  const traverse =
    (delta: number, landing: NavigationState): HistoryOp =>
    () => {
      history.go(delta);
      return depthOf(landing);
    };
  // A reserved entry already sits above the one it was claimed from, so it is reused rather than pushed.
  const advance = (navigation: NavigationState): HistoryOp[] =>
    navigation.reservation === null ? [write({ ...navigation, hasForward: true }), pushEntry] : [];

  const onPopState = (): void => {
    const landed = currentNavigation();
    if (awaitedDepth !== null && depthOf(landed) === awaitedDepth) {
      awaitedDepth = null;
      run();
      return;
    }
    queue.length = 0;
    awaitedDepth = null;
    intended = landed;
    active?.drop();
    active = null;
    if (compact()) {
      setStack(landed.stack);
    }
  };
  window.addEventListener("popstate", onPopState);
  onCleanup(() => window.removeEventListener("popstate", onPopState));

  createEffect(() => {
    if (compact()) {
      setStack(intended.stack);
    }
  });

  const plan = (next: MobileSurface, drill: boolean): Move | null => {
    const from = intended;
    const current = from.stack;
    const top = current.at(-1);
    if (next === top) {
      return null;
    }
    const to =
      (navigation: NavigationState, ...ops: HistoryOp[]) =>
      (): void => {
        intended = navigation;
        run(...ops);
      };
    if (next === "inbox" || (!drill && next === current.at(-2))) {
      const depth = next === "inbox" ? current.length - 1 : 1;
      const landing = { hasForward: true, reservation: null, stack: current.slice(0, -depth) };
      return {
        apply: to(landing, traverse(-depth, landing)),
        stack: landing.stack,
        undo: to(from, traverse(depth, from)),
      };
    }
    const pushes = drill || top === "inbox" || from.hasForward;
    const stack = pushes ? [...current, next] : [...current.slice(0, -1), next];
    const destination = { hasForward: false, reservation: null, stack };
    const below = { ...from, hasForward: true };
    return {
      apply: to(destination, ...(pushes ? advance(from) : []), write(destination)),
      stack,
      undo:
        pushes && from.reservation === null
          ? to(below, traverse(-1, below))
          : to(from, write(from)),
    };
  };

  // Another navigation outranks a gesture still in flight: the gesture lands only if it was heading there.
  const settleActive = (next: MobileSurface | null): void => {
    const previous = active;
    active = null;
    if (previous === null) {
      return;
    }
    previous.drop();
    if (previous.target === next) {
      setStack(previous.move.stack);
    } else {
      previous.move.undo();
    }
  };

  const navigate = (next: MobileSurface, drill: boolean): void => {
    if (!compact()) {
      setStack([next]);
      return;
    }
    settleActive(next);
    const move = plan(next, drill);
    if (move !== null) {
      move.apply();
      setStack(move.stack);
    }
  };

  // WebKit snapshots an entry for its swipe preview as the page leaves it, so a gesture moves history
  // while its starting surface is still on screen and renders the destination only once it commits.
  const preview = (next: MobileSurface): MobilePreview | null => {
    if (!compact()) {
      return null;
    }
    settleActive(null);
    const move = plan(next, false);
    if (move === null) {
      return null;
    }
    const [dropped, setDropped] = createSignal(false);
    const own: ActivePreview = { drop: () => setDropped(true), move, target: next };
    active = own;
    move.apply();
    const settle = (commit: boolean): void => {
      if (active === own) {
        active = null;
        if (commit) {
          setStack(move.stack);
        } else {
          move.undo();
        }
      }
    };
    return { cancel: () => settle(false), commit: () => settle(true), dropped };
  };

  // WebKit's back gesture skips entries pushed without a recent user gesture, so opening a session from
  // the inbox claims its drill-in entry here, inside the tap, and the async open fills it.
  const reserve = (): (() => void) => {
    if (!compact()) {
      return () => {};
    }
    settleActive(null);
    const from = intended;
    const reservation = ++nextReservation;
    const reserved = { hasForward: false, reservation, stack: from.stack };
    intended = reserved;
    run(...advance(from), write(reserved));
    return () => {
      if (intended.reservation === reservation) {
        const below = { hasForward: true, reservation: null, stack: reserved.stack };
        intended = below;
        run(traverse(-1, below));
      }
    };
  };

  return {
    backTarget,
    drill: (next) => navigate(next, true),
    preview,
    reserve,
    select: (next) => navigate(next, false),
    surface,
  };
}

// Entries form one chain from the root: every push adds a surface, except a reservation's duplicate.
function depthOf(navigation: NavigationState): number {
  return navigation.stack.length - 1 + (navigation.reservation === null ? 0 : 1);
}

function readNavigation(state: unknown): NavigationState | null {
  if (state === null || typeof state !== "object") {
    return null;
  }
  const navigation = (state as Record<string, unknown>)[STATE_KEY];
  if (navigation === null || typeof navigation !== "object") {
    return null;
  }
  const { hasForward, reservation, stack } = navigation as Record<string, unknown>;
  if (
    typeof hasForward !== "boolean" ||
    (reservation !== null && typeof reservation !== "number") ||
    !Array.isArray(stack) ||
    stack.length === 0 ||
    stack[0] !== "inbox" ||
    !stack.every(isMobileSurface) ||
    stack.some((surface, index) => index > 0 && surface === stack[index - 1])
  ) {
    return null;
  }
  return { hasForward, reservation, stack };
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
