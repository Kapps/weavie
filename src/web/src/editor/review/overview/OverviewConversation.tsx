import { MessageSquarePlus } from "lucide-solid";
import { createSignal, For, type JSX, Show } from "solid-js";
import { AgentMarkdown } from "../../../agent/AgentMarkdown";
import type { ClientSession } from "../../../bridge";
import { PrCommentComposer } from "../../pr-comments/PrCommentComposer";
import { Avatar } from "../../pr-comments/PrCommentItem";
import { sendPrComment } from "../../pr-comments/pr-comments-store";
import { RelativeTime } from "../../RelativeTime";
import type { PrConversationItem, PrDetail, ReviewerState } from "./overview-data";

const VERDICTS: Record<ReviewerState, string> = {
  approved: "approved",
  changesRequested: "requested changes",
  commented: "reviewed",
  pending: "was asked to review",
};

function Item(props: { session: ClientSession; item: PrConversationItem }): JSX.Element {
  return (
    <article
      class="session-overview-comment"
      classList={{ "session-overview-event": props.item.review !== null }}
    >
      <Avatar user={props.item.author} />
      <div class="session-overview-comment-main">
        <div class="session-overview-comment-head">
          <strong>{props.item.author.login}</strong>
          <span class={`session-overview-verdict-${props.item.review ?? "comment"}`}>
            {props.item.review === null ? "commented" : VERDICTS[props.item.review]}
          </span>
          <span class="session-overview-when">
            <RelativeTime seconds={Date.parse(props.item.createdAt) / 1000} />
          </span>
        </div>
        <Show when={props.item.body !== ""}>
          <div class="session-overview-comment-body">
            <AgentMarkdown
              cacheKey={props.item}
              content={props.item.body}
              flavor="github"
              renderMermaid={false}
              session={props.session}
            />
          </div>
        </Show>
      </div>
    </article>
  );
}

/** PR-level comments and submitted reviews, oldest first, with a composer that posts immediately. */
export function OverviewConversation(props: {
  session: ClientSession;
  detail: PrDetail;
}): JSX.Element {
  const [composing, setComposing] = createSignal(false);
  return (
    <div class="session-overview-conversation">
      <For each={props.detail.conversation}>
        {(item) => <Item session={props.session} item={item} />}
      </For>
      <Show
        when={composing()}
        fallback={
          <button type="button" class="session-overview-compose" onClick={() => setComposing(true)}>
            <MessageSquarePlus size="1em" /> Comment on this pull request…
          </button>
        }
      >
        <PrCommentComposer
          draftKey={`issue:${props.detail.url}`}
          initial=""
          placeholder="Comment on this pull request — posts now, outside any review"
          submitLabel="Comment"
          onSubmit={async (body) => {
            const error = await sendPrComment(props.session, "issueComment", {
              number: props.detail.number,
              body,
            });
            if (error === null) setComposing(false);
            return error;
          }}
          alternate={null}
          note=""
          onCancel={() => setComposing(false)}
        />
      </Show>
    </div>
  );
}
