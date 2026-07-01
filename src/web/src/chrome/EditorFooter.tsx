import { type JSX, Show } from "solid-js";
import { dirtyPaths } from "../editor/dirty-store";
import { editorStatus } from "../editor/editor-status-store";
import { canonicalFsPath } from "../editor/fs-path";
import { activePath } from "../editor/session-store";
import { RecentFilesButton } from "./RecentFilesButton";

/**
 * The editor pane's status bar: cursor position, selection size, unsaved state, line endings, and language on
 * the left/centre (only when a file is in view), and the recent-files control anchored bottom-right. The bar is
 * always present so Recent Files stays reachable even with no file open.
 */
export function EditorFooter(props: {
  onOpenRecent: (path: string) => void;
  root: () => string;
}): JSX.Element {
  // Mirror the tab strip's dirty test (dirty-store is keyed by canonical fs-path).
  const isDirty = (): boolean => {
    const path = activePath();
    return path !== null && dirtyPaths().has(canonicalFsPath(path));
  };
  return (
    <div class="pane-footer">
      <Show when={editorStatus()}>
        {(status) => (
          <span class="footer-seg">
            Ln {status().line}, Col {status().column}
            <Show when={status().selectionCount > 0}> ({status().selectionCount} selected)</Show>
          </span>
        )}
      </Show>
      <span class="footer-spacer" />
      <Show when={isDirty()}>
        <span class="footer-seg footer-accent">Unsaved</span>
      </Show>
      <Show when={editorStatus()}>
        {(status) => (
          <>
            <span class="footer-seg">{status().eol}</span>
            <Show when={status().languageId !== "plaintext"}>
              <span class="footer-seg">{status().languageId}</span>
            </Show>
          </>
        )}
      </Show>
      <RecentFilesButton onOpen={props.onOpenRecent} root={props.root} />
    </div>
  );
}
