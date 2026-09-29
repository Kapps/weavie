import type { FocusIntent, InteractionIntent } from "../../chrome/interaction-intent";
import { notify } from "../../notify/notify";
import { normalizePath } from "../fs-path";
import type { TextLocation } from "../nav-history";
import type { TabPresenter } from "../tab-owner";
import type { ReviewDecisionCompletion } from "./review-decision";
import type { ReviewHorizontalPositions } from "./review-horizontal-position";
import type { ReviewSection } from "./review-section";
import { hasReviewChanges, type ReviewFileView } from "./review-store";
import type { ReviewToolbarPresenter } from "./review-toolbar-presenter";
import type { ReviewToolbarTarget } from "./review-toolbar-state";

interface ReviewViewState {
  location: TextLocation | null;
  scrollTop: number;
  horizontal: Readonly<Record<string, number>>;
}

type ReviewAlignment = "location" | "file-start" | "focus";
interface SectionEntry {
  section: ReviewSection;
}

export interface UnifiedReviewSurface extends Omit<TabPresenter, "signal"> {
  dispose(): void;
  refresh(): void;
  takeControl(): void;
  reveal(path: string, line: number, focus: FocusIntent): void;
  requestFocus(path: string): () => void;
}

export interface ReviewSectionBinding {
  changed(): void;
  dispose(): void;
}

export interface ReviewSectionRegistry {
  bind(path: string, section: ReviewSection): ReviewSectionBinding;
}

/** One request owns activation, placement and focus in an exact retained file. */
export function createReviewSurface(surface: {
  interaction: InteractionIntent;
  horizontal: ReviewHorizontalPositions;
  active(): boolean;
  signal: AbortSignal;
  clear(): void;
  getScrollTop(): number;
  setScrollTop(top: number): void;
  files(): ReviewFileView[];
  /** The active file's index: the selected file, else the first on screen, else undefined. */
  currentIndex(): number | undefined;
  select(index: number, path: string, line: number): void;
  expand(file: ReviewFileView): void;
  scrollToIndex(index: number): void;
  focus(): void;
  controls: Pick<ReviewToolbarPresenter, "refresh" | "captureActions">;
}): UnifiedReviewSurface & { sections: ReviewSectionRegistry; target(): ReviewToolbarTarget } {
  const sections = new Map<string, SectionEntry>();
  const lifetime = new AbortController();
  let pending:
    | {
        location: TextLocation;
        file: ReviewFileView;
        entry: SectionEntry | undefined;
        alignment: ReviewAlignment;
        focus: FocusIntent | undefined;
        positioned: boolean;
        finish(): void;
        fail(error: unknown): void;
        cancel(): void;
      }
    | undefined;
  let applying = false;
  let changedWhileApplying = false;
  const settle = (): void => {
    if (applying) {
      changedWhileApplying = true;
      return;
    }
    const operation = pending;
    if (!operation?.positioned) return;
    if (!surface.files().includes(operation.file)) {
      operation.fail(new Error("This file is no longer in the review."));
      return;
    }
    const entry = sections.get(normalizePath(operation.location.path));
    if (entry === undefined) {
      const file = operation.file;
      const diff = file.diff();
      if (file.collapsed() || (file.loaded() && (!diff || !hasReviewChanges(diff)))) {
        if (operation.focus?.current()) surface.focus();
        operation.finish();
      }
      return;
    }
    operation.entry ??= entry;
    if (operation.entry !== entry) {
      operation.cancel();
      return;
    }
    applying = true;
    try {
      const state = entry.section.state();
      if (state.kind === "collapsed" || state.kind === "empty") {
        if (operation.focus?.current()) surface.focus();
        operation.finish();
        return;
      }
      const navigation =
        state.kind === "ready"
          ? state.enter(operation.alignment === "focus" ? "focus" : "navigate")
          : undefined;
      const capability =
        navigation ??
        (operation.alignment === "focus" && state.kind !== "ready" ? state.input : undefined);
      if (pending !== operation) return;
      if (!capability?.current()) {
        if (state.kind === "unavailable") operation.fail(state.failure.error);
        return;
      }
      if (operation.alignment !== "focus" && navigation) {
        if (operation.alignment === "file-start")
          navigation.revealFileStart(operation.location.line);
        else navigation.restore(operation.location);
      }
      if (pending !== operation || !capability.current()) return;
      if (operation.focus?.current()) capability.focus();
      if (pending === operation && capability.current()) operation.finish();
    } catch (error) {
      if (pending === operation) operation.fail(error);
    } finally {
      applying = false;
      if (changedWhileApplying && pending) queueMicrotask(settle);
      changedWhileApplying = false;
    }
  };
  const currentFile = (): ReviewFileView | undefined => {
    const index = surface.currentIndex();
    return index === undefined ? undefined : surface.files()[index];
  };
  const activeSection = (): ReviewSection | undefined => {
    const file = currentFile();
    return file && sections.get(normalizePath(file.summary().path))?.section;
  };
  const request = (
    location: TextLocation,
    signal: AbortSignal,
    alignment: ReviewAlignment,
    focus: FocusIntent | undefined,
  ): Promise<void> => {
    pending?.cancel();
    const index = surface
      .files()
      .findIndex((file) => normalizePath(file.summary().path) === normalizePath(location.path));
    const file = surface.files()[index];
    if (file === undefined)
      return Promise.reject(new Error("This file is no longer in the review."));
    const entry = sections.get(normalizePath(location.path));
    const validity = AbortSignal.any([signal, surface.signal, lifetime.signal]);
    return new Promise((resolve, reject) => {
      const complete = (finish: () => void): void => {
        if (pending === operation) pending = undefined;
        validity.removeEventListener("abort", cancel);
        finish();
      };
      const fail = (error: unknown): void => complete(() => reject(error));
      const cancel = (): void =>
        fail(new DOMException("Review navigation cancelled", "AbortError"));
      const operation = {
        location,
        file,
        entry,
        alignment,
        focus,
        positioned: false,
        cancel,
        fail,
        finish: () => complete(resolve),
      };
      pending = operation;
      if (validity.aborted) return cancel();
      validity.addEventListener("abort", cancel, { once: true });
      try {
        surface.select(index, location.path, location.line);
        if (pending !== operation) return;
        const state = entry?.section.state();
        if (state?.kind === "unavailable" && !(alignment === "focus" && state.input))
          state.failure.retry();
        if (alignment === "focus") {
          operation.positioned = true;
          settle();
        } else
          queueMicrotask(() => {
            if (pending !== operation) return;
            surface.scrollToIndex(surface.files().indexOf(file) + 1);
            requestAnimationFrame(() => {
              if (pending !== operation) return;
              operation.positioned = true;
              settle();
            });
          });
      } catch (error) {
        fail(error);
      }
    });
  };
  const report = (result: Promise<void>): void => {
    void result.catch((error: unknown) => {
      if (!(error instanceof DOMException && error.name === "AbortError"))
        notify("warn", String(error));
    });
  };
  const requestFocus = (path: string): (() => void) => {
    const controller = new AbortController();
    report(request({ path, line: 1 }, controller.signal, "focus", surface.interaction.begin()));
    return () => controller.abort();
  };
  const focus = (): void => {
    const state = activeSection()?.state();
    const input = state && "input" in state ? state.input : undefined;
    if (input?.current()) input.focus();
    else surface.focus();
  };
  const reveal = (location: TextLocation, alignment: ReviewAlignment, focus: FocusIntent): void => {
    const file = surface
      .files()
      .find((file) => normalizePath(file.summary().path) === normalizePath(location.path));
    if (file !== undefined) surface.expand(file);
    report(request(location, lifetime.signal, alignment, focus));
  };
  const captureReviewAdvance = (path: string): ReviewDecisionCompletion => {
    const current = currentFile();
    if (current === undefined || normalizePath(current.summary().path) !== normalizePath(path))
      return () => {};
    return (location, focus) => {
      if (
        lifetime.signal.aborted ||
        surface.signal.aborted ||
        !surface.active() ||
        !focus.current()
      )
        return;
      reveal(location, "file-start", focus);
    };
  };
  return {
    text: true,
    captureReviewAdvance,
    capture: () => {
      const file = currentFile();
      const location =
        activeSection()?.capture() ??
        (file === undefined ? null : { path: file.summary().path, line: file.summary().line });
      return {
        state: {
          location,
          scrollTop: surface.getScrollTop(),
          horizontal: surface.horizontal.snapshot(),
        },
        text: location,
      };
    },
    restore: async (placement, signal) => {
      signal.throwIfAborted();
      surface.clear();
      const saved =
        "viewState" in placement ? (placement.viewState as ReviewViewState | null) : null;
      if (saved !== null) surface.horizontal.restore(saved.horizontal);
      if (saved?.location != null) {
        const index = surface
          .files()
          .findIndex(
            (file) => normalizePath(file.summary().path) === normalizePath(saved.location!.path),
          );
        const file = surface.files()[index];
        if (!file) notify("warn", "This saved location is no longer in the review.");
        else if (file.collapsed()) {
          pending?.cancel();
          surface.select(index, saved.location.path, saved.location.line);
          surface.setScrollTop(saved.scrollTop);
        } else await request(saved.location, signal, "location", undefined);
      } else if (saved !== null) surface.setScrollTop(saved.scrollTop);
      signal.throwIfAborted();
    },
    focus,
    requestFocus,
    dispose: () => lifetime.abort(),
    takeControl: () => pending?.cancel(),
    refresh: () => {
      settle();
      surface.controls.refresh();
    },
    actions: surface.controls.captureActions,
    target: () => {
      const state = activeSection()?.state();
      return state?.kind === "ready" || state?.kind === "unavailable"
        ? state.target
        : { kind: "none" };
    },
    reveal: (path, line, focus) => reveal({ path, line }, "location", focus),
    sections: {
      bind: (path, section) => {
        const key = normalizePath(path);
        const entry = { section };
        if (pending && normalizePath(pending.location.path) === key) {
          if (pending.entry) pending.cancel();
          else pending.entry = entry;
        }
        sections.set(key, entry);
        const changed = (): void => {
          if (sections.get(key) !== entry) return;
          settle();
          surface.controls.refresh();
        };
        changed();
        return {
          changed,
          dispose: () => {
            if (sections.get(key) !== entry) return;
            if (pending?.entry === entry) pending.cancel();
            sections.delete(key);
            surface.controls.refresh();
          },
        };
      },
    },
  };
}
