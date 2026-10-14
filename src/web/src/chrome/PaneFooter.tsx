import { type JSX, Show } from "solid-js";
import { type SessionStatusName, selectedSession } from "../bridge";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { gitStatus } from "./git-status-store";
import { currentPullRequestStatus } from "./pull-request-store";
import { reviewBranchTitle } from "./review-branch-title";
import { claudeStatus, STATUS_LABEL, STATUS_SHORT } from "./session-store";
import { UpdateIndicator } from "./UpdateIndicator";

/**
 * The shell pane's status footer, serving both terminal panes: the workspace git branch (and its PR), which
 * reviews the branch on click, plus the Claude session's live status promoted from the header dot.
 */
export function PaneFooter(): JSX.Element {
  const status = (): SessionStatusName | undefined => claudeStatus();
  const pullRequest = () => currentPullRequestStatus(selectedSession())?.pullRequest ?? null;
  return (
    <div class="pane-footer">
      <Show when={gitStatus()?.branch}>
        {(branch) => (
          <button
            type="button"
            class="footer-seg footer-branch"
            title={reviewBranchTitle(pullRequest(), branch())}
            onClick={() => void runCommandWithFeedback(CommandIds.diffAgainstBranchBase)}
          >
            <span class="footer-glyph">⎇</span>
            <span class="footer-branch-name">{branch()}</span>
            <Show when={gitStatus()?.dirty}>
              <span class="footer-accent" title="Uncommitted changes">
                ●
              </span>
            </Show>
            <Show when={pullRequest()}>{(pr) => <span>#{pr().number}</span>}</Show>
          </button>
        )}
      </Show>
      <span class="footer-spacer" />
      <UpdateIndicator />
      <Show when={status() !== undefined}>
        <span class="footer-seg" title={STATUS_LABEL[status() as SessionStatusName]}>
          <span class={`session-status status-${status()}`} />
          {STATUS_SHORT[status() as SessionStatusName]}
        </span>
      </Show>
    </div>
  );
}
