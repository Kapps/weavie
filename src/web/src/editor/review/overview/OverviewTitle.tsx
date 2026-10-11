import { ExternalLink } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../../../bridge";
import { pullRequestStatus } from "../../../chrome/pull-request-store";
import { openUrlExternal } from "../../../terminal/terminal-links";
import { relativeTime } from "../../blame-model";
import { FinishReview } from "../../pr-comments/FinishReview";
import { Avatar } from "../../pr-comments/PrCommentItem";
import type { PrDetail } from "./overview-data";

/** The PR's state as a pill, e.g. "Open" or "Draft". */
export function PrStatePill(props: { session: ClientSession; detail: PrDetail }): JSX.Element {
  const state = () => pullRequestStatus(props.session)?.pullRequest?.state ?? "open";
  const label = () => (props.detail.isDraft && state() === "open" ? "draft" : (state() as string));
  return <span class={`session-overview-pill session-overview-pill-${label()}`}>{label()}</span>;
}

/** The session's name: its PR (title, state, author) or its branch; the review-submit control sits top-right. */
export function OverviewTitle(props: {
  session: ClientSession;
  detail: PrDetail | null;
  /** Whether the review-submit control is here; the review header hosts it once the overview scrolls away. */
  submit: boolean;
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
            <h1 class="session-overview-branch">{branch()}</h1>
            <div class="session-overview-byline">
              <span class="session-overview-pill session-overview-pill-none">No pull request</span>
            </div>
          </div>
        }
      >
        {(detail) => (
          <div class="session-overview-name">
            <h1 class="session-overview-pr-title">
              {detail().title} <span class="session-overview-number">#{detail().number}</span>
            </h1>
            <div class="session-overview-byline">
              <PrStatePill session={props.session} detail={detail()} />
              <Avatar user={detail().author} />
              <span class="session-overview-author">{detail().author.login}</span>
              <span>
                wants to merge <code class="session-overview-ref">{branch()}</code>
              </span>
              <Show when={detail().createdAt !== ""}>
                <span>
                  · opened {relativeTime(Date.parse(detail().createdAt) / 1000, Date.now() / 1000)}
                </span>
              </Show>
              <button
                type="button"
                class="session-overview-link"
                title={detail().url}
                onClick={() => openUrlExternal(detail().url)}
              >
                <ExternalLink size="1em" /> Open on the forge
              </button>
            </div>
          </div>
        )}
      </Show>
      <Show when={props.submit}>
        <FinishReview session={props.session} />
      </Show>
    </header>
  );
}
