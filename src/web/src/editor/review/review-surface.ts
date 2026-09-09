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

type ReviewAlignment = "location" | "file-start";

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
  active(): boolean;
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
    alignment: ReviewAlignment;
    ready: boolean;
    finish(): void;
    fail(error: unknown): void;
    cancel(): void;
  } | null = null;
  // A file's section only publishes once its own diff has painted — including the decorations (e.g. a
  // "new file" badge) a paint adds after its first, pre-paint content-height reading — so its on-screen
  // height is final from that point on.
  const sectionSettled = (file: ReviewFileView): boolean => {
    if (file.collapsed()) return true;
    const diff = file.diff();
    if (!file.loaded()) return false;
    return (
      diff === null || !hasReviewChanges(diff) || sections.has(normalizePath(file.summary().path))
    );
  };
  let selectedFile: ReviewFileView | undefined;
  let selectedPending = false;
  const settle = (): void => {
    if (pending === null || !pending.ready) return;
    const files = surface.files();
    const targetIndex = files.findIndex(
      (file) => normalizePath(file.summary().path) === normalizePath(pending!.location.path),
    );
    const target = targetIndex < 0 ? undefined : files[targetIndex];
    if (target === undefined) {
      pending.fail(new Error("This file is no longer in the review."));
      return;
    }
    // The target's own capture()/restore() reads earlier files' rendered positions directly to anchor its
    // scroll (review-editor.ts) — settle on it only once every file stacked above it is done growing, or
    // that read lands on a still-transient height and the scroll it computes silently falls short once the
    // real height lands later.
    if (files.slice(0, targetIndex).some((file) => !sectionSettled(file))) return;
    const section = sections.get(normalizePath(pending.location.path));
    if (section === undefined) {
      if (!sectionSettled(target)) return;
      const operation = pending;
      pending = null;
      operation.finish();
      return;
    }
    const operation = pending;
    pending = null;
    if (operation.alignment === "file-start") section.revealFileStart(operation.location.line);
    else section.restore(operation.location);
    operation.finish();
  };
  const activeSection = (): ReviewEditor | undefined => {
    const file = surface.files()[surface.currentIndex()];
    return file === undefined ? undefined : sections.get(normalizePath(file.summary().path));
  };
  const restore = (
    location: TextLocation,
    signal: AbortSignal,
    alignment: ReviewAlignment,
  ): Promise<void> => {
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
      const operation = {
        location,
        alignment,
        ready: false,
        cancel,
        fail,
        finish: () => complete(resolve),
      };
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
  const reveal = (location: TextLocation, alignment: ReviewAlignment): void => {
    const file = surface
      .files()
      .find((file) => normalizePath(file.summary().path) === normalizePath(location.path));
    if (file !== undefined) surface.expand(file);
    void restore(location, lifetime.signal, alignment)
      .then(focus)
      .catch((error: unknown) => {
        if (!(error instanceof DOMException && error.name === "AbortError"))
          notify("warn", String(error));
      });
  };
  const advanceReviewedFile = (): void => {
    const files = surface.files();
    const index = surface.currentIndex();
    const file = files[index];
    const isPending = file?.pending() === true;
    const completed = file === selectedFile && selectedPending && !isPending;
    selectedFile = file;
    selectedPending = isPending;
    if (!completed || !surface.active()) return;
    for (let step = 1; step < files.length; step++) {
      const next = files[(index + step) % files.length]!;
      if (!next.pending()) continue;
      const { path, line } = next.summary();
      reveal({ path, line }, "file-start");
      return;
    }
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
          await restore(saved.location, signal, "location");
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
      advanceReviewedFile();
      activeSection()?.inline.refreshPresentation();
    },
    actions: () => activeSection()?.inline.captureActions(),
    reveal: (path, line) => reveal({ path, line }, "location"),
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
