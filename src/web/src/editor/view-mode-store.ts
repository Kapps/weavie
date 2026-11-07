import { createSignal } from "solid-js";
import type { ClientSession } from "../bridge";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import { canonicalFsPath } from "./fs-path";
import { previewKindOf } from "./preview/preview-registry";

export type ViewMode = "source" | "preview";

const modes = createSessionOwnedState<ReadonlyMap<string, ViewMode>>(() => new Map());
const scrolls = new WeakMap<ClientSession, Map<string, number>>();
const [markdownView, setMarkdownView] = createSignal(currentEditorOptions().markdownView);
onEditorOptionsChanged((options) => setMarkdownView(options.markdownView));

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
