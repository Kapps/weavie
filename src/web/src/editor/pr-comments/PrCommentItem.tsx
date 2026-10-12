import { ExternalLink, Pencil, Trash2 } from "lucide-solid";
import { createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import { AgentMarkdown } from "../../agent/AgentMarkdown";
import type { ClientSession } from "../../bridge";
import { notify } from "../../notify/notify";
import { openUrlExternal } from "../../terminal/terminal-links";
import { RelativeTime } from "../RelativeTime";
import { PrCommentComposer } from "./PrCommentComposer";
import { type PrComment, type PrUser, sendPrComment } from "./pr-comments-store";

const seconds = (iso: string): number => Date.parse(iso) / 1000;

// The forge's profile photo; a monogram in a stable per-login hue when there's none or it can't load.
export function Avatar(props: { user: PrUser }): JSX.Element {
  const [broken, setBroken] = createSignal(false);
  const hue = () =>
    [...props.user.login].reduce((sum, char) => (sum * 31 + char.charCodeAt(0)) % 360, 7);
  return (
    <Show
      when={props.user.avatarUrl !== "" && !broken()}
      fallback={
        <span
          class="weavie-pr-avatar"
          style={{ background: `hsl(${hue()} 55% 48%)` }}
          aria-hidden="true"
        >
          {props.user.login.charAt(0).toUpperCase()}
        </span>
      }
    >
      <img
        class="weavie-pr-avatar"
        src={props.user.avatarUrl}
        alt=""
        loading="lazy"
        referrerPolicy="no-referrer"
        onError={() => setBroken(true)}
      />
    </Show>
  );
}

// A comment body; a very long one is capped behind "Show more" so one essay can't bury the code.
function CommentBody(props: { comment: PrComment; session: ClientSession }): JSX.Element {
  const [long, setLong] = createSignal(false);
  const [expanded, setExpanded] = createSignal(false);
  let text!: HTMLDivElement;
  onMount(() => {
    const observer = new ResizeObserver(() => setLong(text.scrollHeight > text.clientHeight + 1));
    observer.observe(text);
    onCleanup(() => observer.disconnect());
  });
  return (
    <div class="weavie-pr-comment-body">
      <div
        ref={text}
        class="weavie-pr-comment-text"
        classList={{ "weavie-pr-comment-text-capped": !expanded(), "weavie-pr-fade": long() }}
      >
        <AgentMarkdown
          cacheKey={props.comment}
          content={props.comment.body}
          renderMermaid={false}
          session={props.session}
        />
      </div>
      <Show when={long() || expanded()}>
        <button type="button" class="weavie-pr-more" onClick={() => setExpanded((open) => !open)}>
          {expanded() ? "Show less" : "Show more"}
        </button>
      </Show>
    </div>
  );
}

export function CommentItem(props: {
  comment: PrComment;
  session: ClientSession;
  number: number;
}): JSX.Element {
  const [editing, setEditing] = createSignal(false);
  const time = () => seconds(props.comment.createdAt);
  const discard = async (): Promise<void> => {
    const error = await sendPrComment(props.session, "discardComment", {
      number: props.number,
      id: props.comment.id,
    });
    if (error !== null) notify("error", `Couldn't discard the comment: ${error}`, "pr-comments");
  };
  return (
    <div
      class="weavie-pr-comment"
      classList={{
        "weavie-pr-comment-mine": props.comment.mine,
        "weavie-pr-comment-draft": props.comment.pending,
      }}
    >
      <Avatar user={{ login: props.comment.author, avatarUrl: props.comment.authorAvatarUrl }} />
      <div class="weavie-pr-comment-main">
        <div class="weavie-pr-comment-meta">
          <span class="weavie-pr-author" title={props.comment.author}>
            {props.comment.author}
          </span>
          <Show
            when={props.comment.pending}
            fallback={
              <span class="weavie-pr-time">
                <RelativeTime seconds={time()} />
                {seconds(props.comment.updatedAt) > time() ? " · edited" : ""}
              </span>
            }
          >
            <span
              class="weavie-pr-badge weavie-pr-badge-pending"
              title="Only you can see this until you submit your review."
            >
              Pending
            </span>
          </Show>
          <span class="weavie-pr-comment-actions">
            <Show when={props.comment.mine && !editing()}>
              <button
                type="button"
                class="weavie-pr-icon-button"
                title="Edit comment"
                aria-label="Edit comment"
                onClick={() => setEditing(true)}
              >
                <Pencil size={12} />
              </button>
            </Show>
            <Show when={props.comment.pending && props.comment.mine && !editing()}>
              <button
                type="button"
                class="weavie-pr-icon-button"
                title="Discard pending comment"
                aria-label="Discard pending comment"
                onClick={() => void discard()}
              >
                <Trash2 size={12} />
              </button>
            </Show>
            <Show when={props.comment.url !== ""}>
              <button
                type="button"
                class="weavie-pr-icon-button"
                title="Open on the forge"
                aria-label="Open on the forge"
                onClick={() => openUrlExternal(props.comment.url)}
              >
                <ExternalLink size={12} />
              </button>
            </Show>
          </span>
        </div>
        <Show
          when={editing()}
          fallback={<CommentBody comment={props.comment} session={props.session} />}
        >
          <PrCommentComposer
            draftKey={`edit:${props.number}:${props.comment.id}`}
            initial={props.comment.body}
            placeholder="Edit comment…"
            submitLabel="Save"
            alternate={null}
            note=""
            onSubmit={async (body) => {
              const error = await sendPrComment(props.session, "editComment", {
                number: props.number,
                id: props.comment.id,
                body,
              });
              if (error === null) setEditing(false);
              return error;
            }}
            onCancel={() => setEditing(false)}
          />
        </Show>
      </div>
    </div>
  );
}
