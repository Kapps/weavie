import { notify } from "../../notify/notify";
import { normalizePath } from "../fs-path";
import type { InlineDiffActions } from "../inline-diff";
import type { NavLocation } from "../nav-history";
import type { ReviewEditor } from "./review-editor";
import { hasReviewChanges, type ReviewFileView } from "./review-store";

export interface UnifiedReviewSurface {
  capture(): NavLocation | undefined;
  restore(location: NavLocation, signal: AbortSignal): Promise<void>;
  focus(): void;
  dispose(): void;
  actions(): InlineDiffActions | undefined;
  toolbarHost(): HTMLElement | null;
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
  toolbarHost(): HTMLElement | null;
  changed(): void;
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
    location: NavLocation;
    ready: boolean;
    finish(): void;
    cancel(): void;
    fail(error: unknown): void;
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
      if (file === undefined || !file.loaded() || (diff != null && hasReviewChanges(diff))) return;
      const operation = pending;
      pending = null;
      surface.focus();
      operation.finish();
      return;
    }
    const operation = pending;
    pending = null;
    section.restore(operation.location);
    section.focus();
    operation.finish();
  };
  const activeSection = (): ReviewEditor | undefined => {
    const file = surface.files()[surface.currentIndex()];
    return file === undefined ? undefined : sections.get(normalizePath(file.summary().path));
  };
  const restore = (location: NavLocation, signal: AbortSignal): Promise<void> => {
    pending?.cancel();
    const index = surface
      .files()
      .findIndex((file) => normalizePath(file.summary().path) === normalizePath(location.path));
    const file = surface.files()[index];
    if (file === undefined)
      return Promise.reject(new Error("This file is no longer in the review."));
    const key = normalizePath(location.path);
    if (failures.has(key)) return Promise.reject(failures.get(key));
    const validity = AbortSignal.any([signal, lifetime.signal]);
    return new Promise((resolve, reject) => {
      const cancel = (): void => {
        if (pending === operation) pending = null;
        validity.removeEventListener("abort", cancel);
        reject(new DOMException("Review navigation cancelled", "AbortError"));
      };
      const operation = {
        location,
        ready: false,
        cancel,
        fail: (error: unknown) => {
          if (pending === operation) pending = null;
          validity.removeEventListener("abort", cancel);
          reject(error);
        },
        finish: () => {
          validity.removeEventListener("abort", cancel);
          resolve();
        },
      };
      pending = operation;
      if (validity.aborted) {
        cancel();
        return;
      }
      validity.addEventListener("abort", cancel, { once: true });
      surface.expand(file);
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
  return {
    capture: () => {
      const file = surface.files()[surface.currentIndex()];
      return (
        activeSection()?.capture() ??
        (file === undefined
          ? undefined
          : {
              kind: "review",
              path: file.summary().path,
              line: file.summary().line,
            })
      );
    },
    restore,
    focus: () => {
      const section = activeSection();
      if (section === undefined) surface.focus();
      else section.focus();
    },
    dispose: () => lifetime.abort(),
    refresh: () => {
      settle();
      activeSection()?.inline.refreshPresentation();
    },
    toolbarHost: surface.toolbarHost,
    actions: () => activeSection()?.inline,
    reveal: (path, line) => {
      void restore({ kind: "review", path, line }, lifetime.signal).catch((error: unknown) => {
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
