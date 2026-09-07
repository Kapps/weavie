import { normalizePath } from "../fs-path";
import type { InlineDiffActions } from "../inline-diff";
import type { ReviewEditor } from "./review-editor";
import type { ReviewFileView } from "./review-store";

export interface UnifiedReviewSurface {
  actions(): InlineDiffActions | undefined;
  toolbarHost(): HTMLElement | null;
  refresh(): void;
  reveal(path: string, line: number): void;
}

export interface ReviewSectionRegistry {
  set(path: string, section: ReviewEditor): void;
  clear(path: string, section: ReviewEditor): void;
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
}): UnifiedReviewSurface & { sections: ReviewSectionRegistry } {
  const sections = new Map<string, ReviewEditor>();
  let pending: { path: string; line: number } | null = null;
  const settle = (): void => {
    if (pending === null) return;
    const section = sections.get(normalizePath(pending.path));
    if (section === undefined) return;
    const { line } = pending;
    pending = null;
    section.reveal(line);
  };
  const activeSection = (): ReviewEditor | undefined => {
    const file = surface.files()[surface.currentIndex()];
    return file === undefined ? undefined : sections.get(normalizePath(file.summary().path));
  };
  return {
    refresh: () => activeSection()?.inline.refreshPresentation(),
    toolbarHost: surface.toolbarHost,
    actions: () => activeSection()?.inline,
    reveal: (path, line) => {
      const index = surface
        .files()
        .findIndex((file) => normalizePath(file.summary().path) === normalizePath(path));
      const file = surface.files()[index];
      if (file === undefined) return;
      pending = { path, line };
      surface.expand(file);
      surface.select(index, path, line);
      queueMicrotask(() => {
        surface.scrollToIndex(index + 1);
        requestAnimationFrame(settle);
      });
    },
    sections: {
      set: (path, section) => {
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
