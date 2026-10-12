import { Check, CircleDashed, CircleMinus, X } from "lucide-solid";
import { For, type JSX } from "solid-js";
import { openUrlExternal } from "../../../terminal/terminal-links";
import { Avatar } from "../../pr-comments/PrCommentItem";
import { RelativeTime } from "../../RelativeTime";
import type { CheckState, PrDetail, ReviewCommit, ReviewerState } from "./overview-data";

const CHECK_ORDER: CheckState[] = ["failure", "pending", "neutral", "success"];

const CHECK_ICON: Record<CheckState, () => JSX.Element> = {
  failure: () => <X size="1em" />,
  pending: () => <CircleDashed size="1em" />,
  neutral: () => <CircleMinus size="1em" />,
  success: () => <Check size="1em" />,
};

const REVIEWER_LABEL: Record<ReviewerState, string> = {
  approved: "Approved",
  changesRequested: "Changes requested",
  commented: "Commented",
  pending: "Pending",
};

/** "4 of 6 passed" — the checks heading's summary. */
export function checksSummary(detail: PrDetail): string {
  const passed = detail.checks.filter((check) => check.state === "success").length;
  return `${passed} of ${detail.checks.length} passed`;
}

/** The head's checks, failing first; each opens its details page. */
export function OverviewChecks(props: { detail: PrDetail }): JSX.Element {
  const sorted = () =>
    [...props.detail.checks].sort(
      (a, b) => CHECK_ORDER.indexOf(a.state) - CHECK_ORDER.indexOf(b.state),
    );
  return (
    <ul class="session-overview-list session-overview-scroll">
      <For each={sorted()} fallback={<li class="session-overview-empty">No checks</li>}>
        {(check) => (
          <li>
            <button
              type="button"
              class={`session-overview-row session-overview-check-${check.state}`}
              disabled={!/^https?:\/\//.test(check.url)}
              title={check.url}
              onClick={() => openUrlExternal(check.url)}
            >
              <span class="session-overview-icon">{CHECK_ICON[check.state]()}</span>
              <span class="session-overview-row-text">{check.name}</span>
            </button>
          </li>
        )}
      </For>
    </ul>
  );
}

/** Each reviewer and where their review stands. */
export function OverviewReviewers(props: { detail: PrDetail }): JSX.Element {
  return (
    <ul class="session-overview-list">
      <For
        each={props.detail.reviewers}
        fallback={<li class="session-overview-empty">No reviewers</li>}
      >
        {(reviewer) => (
          <li class="session-overview-row">
            <Avatar user={reviewer.user} />
            <span class="session-overview-row-text">{reviewer.user.login}</span>
            <span class={`session-overview-row-end session-overview-verdict-${reviewer.state}`}>
              {REVIEWER_LABEL[reviewer.state]}
            </span>
          </li>
        )}
      </For>
    </ul>
  );
}

/** The review's commits, newest first; the list scrolls past ten. */
export function OverviewCommits(props: { commits: ReviewCommit[] }): JSX.Element {
  return (
    <ul class="session-overview-list session-overview-commits session-overview-scroll">
      <For each={props.commits} fallback={<li class="session-overview-empty">No commits yet</li>}>
        {(commit) => (
          <li class="session-overview-row" title={`${commit.summary}\n${commit.author}`}>
            <Avatar user={{ login: commit.author, avatarUrl: "" }} />
            <span class="session-overview-row-text">{commit.summary}</span>
            <code class="session-overview-sha">{commit.sha.slice(0, 7)}</code>
            <span class="session-overview-when">
              <RelativeTime seconds={commit.timeUnix} />
            </span>
          </li>
        )}
      </For>
    </ul>
  );
}
