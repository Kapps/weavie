import { type JSX, Show } from "solid-js";
import { selectedSession } from "../bridge";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { gitStatus } from "./git-status-store";
import { currentPullRequestStatus } from "./pull-request-store";
import { reviewBranchTitle, reviewPullRequestTitle } from "./review-branch-title";
import { sessionInfo } from "./session-store";
import { sessionStatusLabel, sessionTooltip, statusDot } from "./session-summary";
import { UpdateIndicator } from "./UpdateIndicator";

/**
 * The shell pane's status footer, serving both terminal panes: the workspace git branch, which reviews the branch (or
 * its PR) on click, plus the session's effective status shared with the rail and agent header.
 */
export function PaneFooter(): JSX.Element {
  const session = () => sessionInfo(selectedSession());
  const title = (branch: string): string => {
    const pr = currentPullRequestStatus(selectedSession())?.pullRequest ?? null;
    return pr === null ? reviewBranchTitle(branch) : reviewPullRequestTitle(pr);
  };
  return (
    <div class="pane-footer">
      <Show when={gitStatus()?.branch}>
        {(branch) => (
          <button
            type="button"
            class="footer-seg footer-branch"
            title={title(branch())}
            onClick={() => void runCommandWithFeedback(CommandIds.diffAgainstBranchBase)}
          >
            <span class="footer-glyph">⎇</span>
            <span class="footer-branch-name">{branch()}</span>
            <Show when={gitStatus()?.dirty}>
              <span class="footer-accent" title="Uncommitted changes">
                ●
              </span>
            </Show>
          </button>
        )}
      </Show>
      <span class="footer-spacer" />
      <UpdateIndicator />
      <Show when={session()}>
        {(current) => (
          <span class="footer-seg" title={sessionTooltip(current())}>
            <span class={`session-status status-${statusDot(current())}`} />
            {sessionStatusLabel(current())}
          </span>
        )}
      </Show>
    </div>
  );
}
