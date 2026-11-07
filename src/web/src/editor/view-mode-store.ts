import { createSignal } from "solid-js";
import type { ClientSession } from "../bridge";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import { canonicalFsPath } from "./fs-path";
import { previewKindOf } from "./preview/preview-registry";

export type ViewMode = "source" | "preview";

const modes = createSessionOwnedState<ReadonlyMap<string, ViewMode>>(() => new Map());
const scrolls = new WeakMap<ClientSession, Map<string, number>>();
const changesOnly = createSessionOwnedState<ReadonlySet<string>>(() => new Set());
const [markdownView, setMarkdownView] = createSignal(currentEditorOptions().markdownView);
const [changeMarks, setChangeMarks] = createSignal(currentEditorOptions().markdownChangeMarks);
onEditorOptionsChanged((options) => {
  setMarkdownView(options.markdownView);
  setChangeMarks(options.markdownChangeMarks);
});

/** Whether a rendered file's changes show word marks on every block or only the current one (reactive). */
export const markdownChangeMarks = changeMarks;

/** Whether `path`'s preview folds the blocks its pending changes don't touch. */
export function isChangesOnly(session: ClientSession, path: string): boolean {
  return changesOnly.get(session)?.has(canonicalFsPath(path)) ?? false;
}

export function toggleChangesOnly(session: ClientSession, path: string): void {
  const key = canonicalFsPath(path);
  const next = new Set(changesOnly.get(session));
  if (!next.delete(key)) next.add(key);
  changesOnly.update(session, () => next);
}

/** A file shows its explicitly chosen mode; Markdown otherwise follows `editor.markdownView`. */
export function isPreviewMode(session: ClientSession, path: string): boolean {
  const chosen = modes.get(session)?.get(canonicalFsPath(path));
  return (chosen ?? (previewKindOf(path) === "markdown" ? markdownView() : "source")) === "preview";
}

export function setViewMode(session: ClientSession, path: string, mode: ViewMode): void {
  const next = new Map(modes.get(session));
  next.set(canonicalFsPath(path), mode);
  modes.update(session, () => next);
}

/** A file opened to review its changes: Markdown reviews them rendered; any other rendered view needs Source. */
export function showForReview(session: ClientSession, path: string): void {
  if (previewKindOf(path) !== "markdown") setViewMode(session, path, "source");
}

export function toggleViewMode(session: ClientSession, path: string): ViewMode {
  const mode = isPreviewMode(session, path) ? "source" : "preview";
  setViewMode(session, path, mode);
  return mode;
}

/** Where `path`'s preview was last scrolled to in this session. */
export function previewScroll(session: ClientSession, path: string): number {
  return scrolls.get(session)?.get(canonicalFsPath(path)) ?? 0;
}

export function rememberPreviewScroll(session: ClientSession, path: string, top: number): void {
  const paths = scrolls.get(session) ?? new Map<string, number>();
  scrolls.set(session, paths.set(canonicalFsPath(path), top));
}
