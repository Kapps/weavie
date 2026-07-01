import { type JSX, Show } from "solid-js";
import { dirtyPaths } from "../editor/dirty-store";
import { editorStatus } from "../editor/editor-status-store";
import { canonicalFsPath } from "../editor/fs-path";
import { activePath } from "../editor/session-store";

/** The editor pane's status footer: cursor position, selection size, unsaved state, line endings, and language. */
export function EditorFooter(): JSX.Element {
  // Mirror the tab strip's dirty test (dirty-store is keyed by canonical fs-path).
  const isDirty = (): boolean => {
    const path = activePath();
    return path !== null && dirtyPaths().has(canonicalFsPath(path));
  };
  return (
    <Show when={editorStatus()}>
      {(status) => (
        <div class="pane-footer">
          <span class="footer-seg">
            Ln {status().line}, Col {status().column}
            <Show when={status().selectionCount > 0}> ({status().selectionCount} selected)</Show>
          </span>
          <span class="footer-spacer" />
          <Show when={isDirty()}>
            <span class="footer-seg footer-accent">Unsaved</span>
          </Show>
          <span class="footer-seg">{status().eol}</span>
          <Show when={status().languageId !== "plaintext"}>
            <span class="footer-seg">{status().languageId}</span>
          </Show>
        </div>
      )}
    </Show>
  );
}
