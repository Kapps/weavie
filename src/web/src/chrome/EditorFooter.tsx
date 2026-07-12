import { TriangleAlert } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import { activeBackendPhase } from "../bridge";
import { isDirtyPath } from "../editor/dirty-store";
import { editorStatus } from "../editor/editor-status-store";
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
  const isDirty = (): boolean => {
    const path = activePath();
    return path !== null && isDirtyPath(path);
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
      <Show when={activeBackendPhase() === "reconnecting"}>
        <span
          class="footer-seg footer-network-problem"
          role="status"
          title="The connection to this workspace was interrupted. Weavie is retrying."
        >
          <TriangleAlert size={13} aria-hidden="true" />
          Network Problems
        </span>
      </Show>
      <Show when={isDirty()}>
        <span class="footer-seg footer-accent">Unsaved</span>
      </Show>
      <Show when={editorStatus()}>
        {(status) => <span class="footer-seg">{status().eol}</span>}
      </Show>
      <RecentFilesButton onOpen={props.onOpenRecent} root={props.root} />
    </div>
  );
}
