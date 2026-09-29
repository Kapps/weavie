import { setContext } from "../../commands/context";
import type {
  InlineDiffActions,
  ReviewHistoryHandlers,
  ReviewHistoryState,
  ReviewScopeState,
} from "../inline-diff";
import type { ReviewFileCommands } from "./review-file-actions";
import { mountReviewToolbar } from "./review-toolbar";
import type { ReviewToolbarCommands, ReviewToolbarTarget } from "./review-toolbar-state";
import { createReviewToolbarView, type ReviewToolbarView } from "./review-toolbar-view";

export interface ReviewToolbarPresenter {
  refresh(): void;
  captureActions(): InlineDiffActions;
  reset(): void;
  dispose(): void;
}

const run = (action: (() => void) | undefined): boolean => {
  if (action === undefined) return false;
  action();
  return true;
};

/** One command/toolbar lifetime per review surface, independent of code paint adapters. */
export function createReviewToolbarPresenter(state: {
  host(): HTMLElement | null;
  active(): boolean;
  scope: ReviewScopeState;
  target(): ReviewToolbarTarget;
  reviewPending(): boolean;
  composerFocused(): boolean;
  history(): ReviewHistoryState;
  historyHandlers(): ReviewHistoryHandlers | undefined;
}): ReviewToolbarPresenter {
  let disposed = false;
  let revision = 0;
  let key: unknown[] = [];
  let view: ReviewToolbarView | undefined;
  const menu = { open: false };
  const current = (): ReviewToolbarTarget => (disposed ? { kind: "none" } : state.target());
  const reviewUp = (): boolean => {
    const target = current();
    return (
      state.reviewPending() ||
      target.kind === "parked" ||
      (target.kind === "file" &&
        target.paint.status !== "pending" &&
        target.paint.options.mode === "applied") ||
      state.history().canUndo
    );
  };
  const historyActions = () => {
    const handlers = state.historyHandlers();
    const valid = (): boolean => !disposed && state.historyHandlers() === handlers;
    return {
      undoKeep: (): boolean =>
        valid() && reviewUp() && (state.history().canUndoKeep ? run(handlers?.onUndoKeep) : true),
      undoRevert: (): boolean =>
        valid() &&
        reviewUp() &&
        (state.history().canUndoRevert ? run(handlers?.onUndoRevert) : true),
      redoReview: (): boolean => valid() && state.history().canRedo && run(handlers?.onRedo),
    };
  };
  const locationAction = (
    name: Exclude<keyof InlineDiffActions, "undoKeep" | "undoRevert" | "redoReview">,
  ): boolean => {
    const target = current();
    if (target.kind === "file")
      return target.document.actions.commands(target.presentation)[name]();
    if (target.kind !== "parked" || state.composerFocused()) return false;
    const navigation = target.summary;
    switch (name) {
      case "accept":
      case "nextChange":
      case "prevChange":
        return run(navigation.stepIn);
      case "nextFile":
        return run(navigation.nextFile);
      case "prevFile":
        return run(navigation.prevFile);
      default:
        return false;
    }
  };
  const locationCommands = (): ReviewFileCommands => ({
    nextChange: () => locationAction("nextChange"),
    prevChange: () => locationAction("prevChange"),
    nextFile: () => locationAction("nextFile"),
    prevFile: () => locationAction("prevFile"),
    accept: () => locationAction("accept"),
    reject: () => locationAction("reject"),
    undo: () => locationAction("undo"),
    keepFile: () => locationAction("keepFile"),
    revertFile: () => locationAction("revertFile"),
    keepAll: () => locationAction("keepAll"),
    comment: () => locationAction("comment"),
  });
  const commands = (): ReviewToolbarCommands => ({
    ...locationCommands(),
    undoKeep: () => historyActions().undoKeep(),
    undoRevert: () => historyActions().undoRevert(),
    redoReview: () => historyActions().redoReview(),
    undoLast: () =>
      !disposed && state.history().canUndo && run(state.historyHandlers()?.onUndoLast),
  });
  const index = (): number => {
    const target = current();
    if (target.kind !== "file" || target.paint.status !== "ready") return -1;
    const hunks = target.paint.markers.hunks;
    const line = target.presentation.reviewLine();
    let found = hunks.length === 0 ? -1 : 0;
    for (let i = 0; i < hunks.length; i++) {
      if (hunks[i]!.anchorLine > line) break;
      found = i;
    }
    return found;
  };
  const stale = (): boolean => {
    const target = current();
    return (
      target.kind === "file" &&
      target.paint.status === "ready" &&
      (!target.presentation.valid() ||
        target.document.actions.stale ||
        target.document.actions.options !== target.paint.options ||
        target.document.actions.geometry?.markers !== target.paint.markers)
    );
  };
  const targetKey = (target: ReviewToolbarTarget): unknown[] => {
    if (target.kind === "none") return [undefined];
    if (target.kind === "parked") return [target.summary];
    const paint = target.paint;
    return [
      target.document,
      target.owner,
      paint.status,
      paint.status === "pending" ? undefined : paint.options,
      paint.status === "ready"
        ? paint.markers
        : paint.status === "unavailable"
          ? paint.message
          : undefined,
      state.scope.current,
    ];
  };
  const refresh = (): void => {
    if (disposed) return;
    const target = current();
    if (
      state.active() &&
      target.kind === "file" &&
      target.paint.status === "ready" &&
      target.paint.options.mode === "applied" &&
      target.document.actions.options?.allActionsDisabled === true &&
      state.scope.current === "all"
    )
      state.scope.current = "change";
    const nextKey = targetKey(target);
    if (nextKey.length !== key.length || nextKey.some((item, index) => item !== key[index])) {
      if (key[0] !== nextKey[0]) menu.open = false;
      key = nextKey;
      revision++;
      const previous = view;
      view = undefined;
      const paint =
        target.kind === "parked"
          ? { status: "parked" as const, summary: target.summary }
          : target.kind === "file"
            ? target.paint
            : { status: "pending" as const };
      if (paint.status !== "pending") {
        const generation = revision;
        const validView = (): boolean => {
          const latest = targetKey(current());
          return (
            !disposed &&
            state.active() &&
            generation === revision &&
            latest.length === nextKey.length &&
            latest.every((value, index) => value === nextKey[index])
          );
        };
        const guarded = Object.fromEntries(
          Object.entries(commands()).map(([name, action]) => [name, () => validView() && action()]),
        ) as unknown as ReviewToolbarCommands;
        view = createReviewToolbarView({
          paint,
          scope: state.scope,
          menu,
          commands: guarded,
          setScope: (scope) => {
            if (!validView()) return;
            const file = current();
            if (
              file.kind !== "file" ||
              file.paint.status !== "ready" ||
              file.paint.options.mode !== "applied"
            )
              return;
            if (scope === "all" && file.document.actions.options?.allActionsDisabled === true)
              return;
            state.scope.current = scope;
            refresh();
          },
          index,
          stale,
          history: state.history,
        });
      }
      previous?.bar.remove();
    }
    const host = state.active() ? state.host() : null;
    if (view !== undefined) {
      if (host === null) view.bar.remove();
      else if (view.bar.parentElement !== host) mountReviewToolbar(host, view.bar);
      view.refresh();
    }
    if (state.active()) setContext("diffActive", target.kind !== "none");
  };
  const closeMenu = (): void => {
    menu.open = false;
    view?.refresh();
  };
  const onPointer = (event: PointerEvent): void => {
    if (menu.open && !view?.menuContains(event.composedPath())) closeMenu();
  };
  const onKey = (event: KeyboardEvent): void => {
    if (state.active() && menu.open && event.key === "Escape") {
      event.stopPropagation();
      closeMenu();
    }
  };
  document.addEventListener("pointerdown", onPointer, true);
  document.addEventListener("keydown", onKey, true);
  const reset = (): void => {
    revision++;
    key = [];
    menu.open = false;
    view?.bar.remove();
    view = undefined;
  };
  return {
    refresh,
    reset,
    captureActions: () => {
      const target = current();
      const generation = revision;
      const scope = state.scope.current;
      const location =
        target.kind === "file"
          ? target.document.actions.capture(target.presentation)
          : locationCommands();
      const captured = Object.fromEntries(
        Object.entries(location).map(([name, action]) => [
          name,
          () => {
            const next = current();
            if (
              disposed ||
              generation !== revision ||
              scope !== state.scope.current ||
              next.kind !== target.kind ||
              (next.kind === "file" &&
                target.kind === "file" &&
                (next.document !== target.document || next.owner !== target.owner)) ||
              (next.kind === "parked" &&
                target.kind === "parked" &&
                next.summary !== target.summary)
            )
              throw new Error("The review location for this command has changed.");
            return action();
          },
        ]),
      ) as Omit<InlineDiffActions, "undoKeep" | "undoRevert" | "redoReview">;
      return { ...captured, ...historyActions() };
    },
    dispose: () => {
      disposed = true;
      reset();
      document.removeEventListener("pointerdown", onPointer, true);
      document.removeEventListener("keydown", onKey, true);
    },
  };
}
