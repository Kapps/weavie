import { GitCompare } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../../../bridge";
import { pullRequestStatus } from "../../../chrome/pull-request-store";
import { keyHint } from "../../../commands/key-hint";
import { runCommandWithFeedback } from "../../../commands/registry";
import { CommandIds } from "../../../commands/types";
import { openUrlExternal } from "../../../terminal/terminal-links";
import { FinishReview } from "../../pr-comments/FinishReview";
import { Avatar } from "../../pr-comments/PrCommentItem";
import { RelativeTime } from "../../RelativeTime";
import type { PrDetail } from "./overview-data";

function ReviewBranchLink(props: { shown: boolean }): JSX.Element {
  return (
    <Show when={props.shown}>
      <button
        type="button"
        class="session-overview-link"
        title={`Review everything this branch changes${keyHint(CommandIds.diffAgainstBranchBase)}`}
        onClick={() => void runCommandWithFeedback(CommandIds.diffAgainstBranchBase)}
      >
        <GitCompare size="1em" /> Review branch
      </button>
    </Show>
  );
}

/** The PR's state as a pill, e.g. "Open" or "Draft". */
export function PrStatePill(props: { session: ClientSession; detail: PrDetail }): JSX.Element {
  const state = () => pullRequestStatus(props.session)?.pullRequest?.state ?? "open";
  const label = () => (props.detail.isDraft && state() === "open" ? "draft" : (state() as string));
  return <span class={`session-overview-pill session-overview-pill-${label()}`}>{label()}</span>;
}

/** The session's name: its PR (title, state, author, branches) or its branch, with the review-submit control. */
export function OverviewTitle(props: {
  session: ClientSession;
  detail: PrDetail | null;
  /** Whether the review-submit control is here; the review header hosts it once the overview scrolls away. */
  submit: boolean;
  /** Whether the review already compares the whole branch, so offering to is moot. */
  branchReview: boolean;
  /** Receives the title line, whose height never depends on where the submit control sits. */
  ref: (element: HTMLElement) => void;
}): JSX.Element {
  const branch = (): string => {
    const status = pullRequestStatus(props.session);
    return status === null ? "" : (status.branch ?? "detached HEAD");
  };
  return (
    <header class="session-overview-top">
      <Show
        when={props.detail}
        fallback={
          <div class="session-overview-name">
            <h1 class="session-overview-branch" ref={props.ref}>
              {branch()}
            </h1>
            <div class="session-overview-byline">
              <span class="session-overview-pill session-overview-pill-none">No pull request</span>
              <ReviewBranchLink shown={!props.branchReview} />
            </div>
          </div>
        }
      >
        {(detail) => (
          <div class="session-overview-name">
            <h1 class="session-overview-pr-title" ref={props.ref}>
              {detail().title}{" "}
              <button
                type="button"
                class="session-overview-number"
                title={`Open #${detail().number} on GitHub — ${detail().url}`}
                onClick={() => openUrlExternal(detail().url)}
              >
                #{detail().number}
              </button>
            </h1>
            <div class="session-overview-byline">
              <PrStatePill session={props.session} detail={detail()} />
              <Avatar user={detail().author} />
              <span class="session-overview-author">{detail().author.login}</span>
              <span>
                wants to merge <code class="session-overview-ref">{detail().headRef}</code> into{" "}
                <code class="session-overview-ref">{detail().baseRef}</code>
              </span>
              <Show when={detail().createdAt !== ""}>
                <span>
                  · opened <RelativeTime seconds={Date.parse(detail().createdAt) / 1000} />
                </span>
              </Show>
              <ReviewBranchLink shown={!props.branchReview} />
              <Show when={props.submit}>
                <FinishReview session={props.session} />
              </Show>
            </div>
          </div>
        )}
      </Show>
    </header>
  );
}
