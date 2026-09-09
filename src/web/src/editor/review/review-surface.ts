import { notify } from "../../notify/notify";
import { normalizePath } from "../fs-path";
import type { TextLocation } from "../nav-history";
import type { TabPresenter } from "../tab-owner";
import type { ReviewEditor } from "./review-editor";
import { hasReviewChanges, type ReviewFileView } from "./review-store";

interface ReviewViewState {
  location: TextLocation | null;
  scrollTop: number;
}

export interface UnifiedReviewSurface extends Omit<TabPresenter, "signal"> {
  dispose(): void;
  refresh(): void;
  reveal(path: string, line: number): void;
}

export interface ReviewSectionRegistry {
  set(path: string, section: ReviewEditor): void;
  clear(path: string, section: ReviewEditor): void;
  empty(path: string): void;
  failed(path: string, error: unknown): void;
}

/** Resolves exact file destinations through the virtualizer; hunk navigation belongs to InlineDiff. */
export function createReviewSurface(surface: {
  changed(): void;
  signal: AbortSignal;
  clear(): void;
  scroller(): HTMLElement;
  files(): ReviewFileView[];
  currentIndex(): number;
  select(index: number, path: string, line: number): void;
  expand(file: ReviewFileView): void;
  scrollToIndex(index: number): void;
  focus(): void;
}): UnifiedReviewSurface & { sections: ReviewSectionRegistry } {
  const sections = new Map<string, ReviewEditor>();
  const lifetime = new AbortController();
  const failures = new Map<string, unknown>();
  let pending: {
    location: TextLocation;
    ready: boolean;
    finish(): void;
    fail(error: unknown): void;
    cancel(): void;
  } | null = null;
  const settle = (): void => {
    if (pending === null || !pending.ready) return;
    const section = sections.get(normalizePath(pending.location.path));
    if (section === undefined) {
      const file = surface
        .files()
        .find(
          (candidate) =>
            normalizePath(candidate.summary().path) === normalizePath(pending!.location.path),
        );
      const diff = file?.diff();
      if (file === undefined) {
        pending.fail(new Error("This file is no longer in the review."));
        return;
      }
      if (!file.collapsed() && (!file.loaded() || (diff != null && hasReviewChanges(diff)))) return;
      const operation = pending;
      pending = null;
      operation.finish();
      return;
    }
    const operation = pending;
    pending = null;
    section.restore(operation.location);
    operation.finish();
  };
  const activeSection = (): ReviewEditor | undefined => {
    const file = surface.files()[surface.currentIndex()];
    return file === undefined ? undefined : sections.get(normalizePath(file.summary().path));
  };
  const restore = (location: TextLocation, signal: AbortSignal): Promise<void> => {
    pending?.cancel();
    const index = surface
      .files()
      .findIndex((file) => normalizePath(file.summary().path) === normalizePath(location.path));
    const file = surface.files()[index];
    if (file === undefined)
      return Promise.reject(new Error("This file is no longer in the review."));
    const key = normalizePath(location.path);
    if (failures.has(key)) return Promise.reject(failures.get(key));
    const validity = AbortSignal.any([signal, surface.signal, lifetime.signal]);
    return new Promise((resolve, reject) => {
      const complete = (settle: () => void): void => {
        if (pending === operation) pending = null;
        validity.removeEventListener("abort", cancel);
        settle();
      };
      const fail = (error: unknown): void => complete(() => reject(error));
      const cancel = (): void =>
        fail(new DOMException("Review navigation cancelled", "AbortError"));
      const operation = { location, ready: false, cancel, fail, finish: () => complete(resolve) };
      pending = operation;
      if (validity.aborted) {
        cancel();
        return;
      }
      validity.addEventListener("abort", cancel, { once: true });
      surface.select(index, location.path, location.line);
      queueMicrotask(() => {
        if (pending !== operation) return;
        surface.scrollToIndex(index + 1);
        requestAnimationFrame(() => {
          if (pending !== operation) return;
          operation.ready = true;
          settle();
        });
      });
    });
  };
  const focus = (): void => {
    const section = activeSection();
    if (section === undefined) surface.focus();
    else section.focus();
  };
  return {
    text: true,
    capture: () => {
      const file = surface.files()[surface.currentIndex()];
      const location =
        activeSection()?.capture() ??
        (file === undefined ? null : { path: file.summary().path, line: file.summary().line });
      return { state: { location, scrollTop: surface.scroller().scrollTop }, text: location };
    },
    restore: async (placement, signal) => {
      signal.throwIfAborted();
      surface.clear();
      const saved =
        "viewState" in placement ? (placement.viewState as ReviewViewState | null) : null;
      if (saved?.location != null) {
        if (
          surface
            .files()
            .some(
              (file) => normalizePath(file.summary().path) === normalizePath(saved.location!.path),
            )
        ) {
          await restore(saved.location, signal);
        } else {
          notify("warn", "This saved location is no longer in the review.");
        }
      } else if (saved !== null) {
        surface.scroller().scrollTop = saved.scrollTop;
      }
      signal.throwIfAborted();
    },
    focus,
    dispose: () => lifetime.abort(),
    refresh: () => {
      settle();
      activeSection()?.inline.refreshPresentation();
    },
    actions: () => activeSection()?.inline.captureActions(),
    reveal: (path, line) => {
      const file = surface
        .files()
        .find((file) => normalizePath(file.summary().path) === normalizePath(path));
      if (file !== undefined) surface.expand(file);
      void restore({ path, line }, lifetime.signal)
        .then(focus)
        .catch((error: unknown) => {
          if (!(error instanceof DOMException && error.name === "AbortError"))
            notify("warn", String(error));
        });
    },
    sections: {
      empty: () => settle(),
      failed: (path, error) => {
        failures.set(normalizePath(path), error);
        if (pending !== null && normalizePath(pending.location.path) === normalizePath(path))
          pending.fail(error);
      },
      set: (path, section) => {
        failures.delete(normalizePath(path));
        sections.set(normalizePath(path), section);
        settle();
        surface.changed();
      },
      clear: (path, section) => {
        const key = normalizePath(path);
        if (sections.get(key) === section) {
          sections.delete(key);
          surface.changed();
        }
      },
    },
  };
}
