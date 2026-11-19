import { type JSX, Show } from "solid-js";
import { backendIdOf, type ClientSession } from "../bridge";
import { gitStatusOf } from "./git-status-store";
import { currentPullRequestStatus } from "./pull-request-store";
import { reviewBranchOf, reviewBranchTitle, reviewPullRequestTitle } from "./review-branch-title";
import { sessionInfo } from "./session-store";
import { sessionStatusLabel, sessionTooltip, statusDot } from "./session-summary";
import { UpdateIndicator } from "./UpdateIndicator";

/**
 * The shell pane's status footer, serving both terminal panes: the workspace git branch, which reviews the branch (or
 * its PR) on click, plus the session's effective status shared with the rail and agent header.
 */
export function PaneFooter(props: { session: ClientSession | null }): JSX.Element {
  const session = () => sessionInfo(props.session);
  const gitStatus = () => gitStatusOf(props.session);
  const title = (branch: string): string => {
    const pr = currentPullRequestStatus(props.session)?.pullRequest ?? null;
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
            onClick={() => reviewBranchOf(props.session)}
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
      <UpdateIndicator backendId={backendIdOf(props.session)} />
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
