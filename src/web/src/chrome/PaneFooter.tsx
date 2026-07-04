import { type JSX, Show } from "solid-js";
import type { SessionStatusName } from "../bridge";
import { gitStatus } from "./git-status-store";
import { claudeStatus, STATUS_LABEL, STATUS_SHORT } from "./session-store";

/**
 * The shell pane's status footer, serving both terminal panes: the workspace git branch plus the Claude
 * session's live status promoted from the header dot to a labelled segment.
 */
export function PaneFooter(): JSX.Element {
  const status = (): SessionStatusName | undefined => claudeStatus();
  return (
    <div class="pane-footer">
      <Show when={gitStatus()?.branch}>
        {(branch) => (
          <span class="footer-seg footer-branch" title={`Branch: ${branch()}`}>
            <span class="footer-glyph">⎇</span>
            <span class="footer-branch-name">{branch()}</span>
            <Show when={gitStatus()?.dirty}>
              <span class="footer-accent" title="Uncommitted changes">
                ●
              </span>
            </Show>
          </span>
        )}
      </Show>
      <span class="footer-spacer" />
      <Show when={status() !== undefined}>
        <span class="footer-seg" title={STATUS_LABEL[status() as SessionStatusName]}>
          <span class={`session-status status-${status()}`} />
          {STATUS_SHORT[status() as SessionStatusName]}
        </span>
      </Show>
    </div>
  );
}
