import { ChevronRight, ChevronUp, ExternalLink, MessageSquare, Pencil } from "lucide-solid";
import { createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { AgentMarkdown } from "../../agent/AgentMarkdown";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import { openUrlExternal } from "../../terminal/terminal-links";
import { relativeTime } from "../blame-model";
import type { ThreadPlacement } from "./anchor-map";
import { PrCommentComposer } from "./PrCommentComposer";
import { type PrComment, type PrUser, sendPrComment } from "./pr-comments-store";

const NOTES = {
  outdated: ["Outdated", "The PR's code has changed since this comment was made."],
  removed: ["Removed line", "This comment is on a line the PR removes."],
  changed: ["Changed locally", "Your copy no longer has the line this comment is on."],
} as const;

// A thread longer than this shows its opening comment and latest replies, folding the middle.
const FOLD_AFTER = 4;
const FOLD_KEEP_LATEST = 2;

const seconds = (iso: string): number => Date.parse(iso) / 1000;

// The forge's profile photo; a monogram in a stable per-login hue when there's none or it can't load.
function Avatar(props: { user: PrUser }): JSX.Element {
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

function CommentItem(props: {
  comment: PrComment;
  session: ClientSession;
  number: number;
}): JSX.Element {
  const [editing, setEditing] = createSignal(false);
  const time = () => seconds(props.comment.createdAt);
  return (
    <div class="weavie-pr-comment" classList={{ "weavie-pr-comment-mine": props.comment.mine }}>
      <Avatar user={{ login: props.comment.author, avatarUrl: props.comment.avatarUrl }} />
      <div class="weavie-pr-comment-main">
        <div class="weavie-pr-comment-meta">
          <span class="weavie-pr-author" title={props.comment.author}>
            {props.comment.author}
          </span>
          <span class="weavie-pr-time" title={new Date(time() * 1000).toLocaleString()}>
            {relativeTime(time(), Date.now() / 1000)}
            {seconds(props.comment.updatedAt) > time() ? " · edited" : ""}
          </span>
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

/** One review thread: its comments, then a reply box. Collapsed, it's a one-line summary. */
export function PrThreadCard(props: {
  placement: ThreadPlacement;
  session: ClientSession;
  number: number;
  viewer: PrUser;
  collapsed: boolean;
  onCollapsed: (collapsed: boolean) => void;
  replying: boolean;
  onReplying: (open: boolean) => void;
}): JSX.Element {
  const [unfolded, setUnfolded] = createSignal(false);
  const thread = () => props.placement.thread;
  const note = () => (props.placement.note === null ? null : NOTES[props.placement.note]);
  const participants = () => [
    ...new Map(
      thread().comments.map((c) => [c.author, { login: c.author, avatarUrl: c.avatarUrl }]),
    ).values(),
  ];
  const folded = () =>
    unfolded() || thread().comments.length <= FOLD_AFTER
      ? 0
      : thread().comments.length - 1 - FOLD_KEEP_LATEST;
  const ids = (comments: PrComment[]) => comments.map((comment) => comment.id);
  const latest = () => ids(thread().comments.slice(folded() === 0 ? 1 : -FOLD_KEEP_LATEST));
  // Rows are keyed by comment id: each push rebuilds the comment objects, and that must never remount an
  // open edit box or hand it a different comment.
  const item = (id: number) => (
    <Show when={thread().comments.find((comment) => comment.id === id)}>
      {(comment) => (
        <CommentItem comment={comment()} session={props.session} number={props.number} />
      )}
    </Show>
  );
  return (
    <div class="weavie-pr-card" classList={{ "weavie-pr-card-collapsed": props.collapsed }}>
      <Show
        when={!props.collapsed}
        fallback={
          <button
            type="button"
            class="weavie-pr-summary"
            aria-expanded="false"
            onClick={() => props.onCollapsed(false)}
          >
            <ChevronRight size={14} />
            <span class="weavie-pr-avatars">
              <For each={participants()}>{(user) => <Avatar user={user} />}</For>
            </span>
            <span class="weavie-pr-summary-text">
              <b>{thread().comments[0]?.author}</b> {thread().comments[0]?.body.split("\n")[0]}
            </span>
            <Show when={note()}>
              {(labels) => <span class="weavie-pr-badge">{labels()[0]}</span>}
            </Show>
            <span class="weavie-pr-summary-count">
              <MessageSquare size={12} /> {thread().comments.length}
            </span>
          </button>
        }
      >
        <button
          type="button"
          class="weavie-pr-collapse"
          title="Collapse thread"
          aria-label="Collapse thread"
          aria-expanded="true"
          onClick={() => props.onCollapsed(true)}
        >
          <ChevronUp size={14} />
        </button>
        <Show when={note()}>
          {(labels) => (
            <div class="weavie-pr-context">
              <span class="weavie-pr-badge" title={labels()[1]}>
                {labels()[0]}
              </span>
              <Show when={props.placement.quote}>
                {(quote) => <code class="weavie-pr-quote">{quote()}</code>}
              </Show>
            </div>
          )}
        </Show>
        <For each={ids(thread().comments.slice(0, 1))}>{item}</For>
        <Show when={folded() > 0}>
          <button type="button" class="weavie-pr-fold" onClick={() => setUnfolded(true)}>
            Show {folded()} more {folded() === 1 ? "reply" : "replies"}
          </button>
        </Show>
        <For each={latest()}>{item}</For>
        <div class="weavie-pr-card-foot">
          <Avatar user={props.viewer} />
          <Show
            when={props.replying}
            fallback={
              <button
                type="button"
                class="weavie-pr-reply-stub"
                title={`Reply${keyHint(CommandIds.prComment)}`}
                onClick={() => props.onReplying(true)}
              >
                Reply…
              </button>
            }
          >
            <PrCommentComposer
              draftKey={`reply:${props.number}:${thread().rootId}`}
              initial=""
              placeholder="Reply…"
              submitLabel="Reply"
              onSubmit={async (body) => {
                const error = await sendPrComment(props.session, "reply", {
                  number: props.number,
                  inReplyTo: thread().rootId,
                  body,
                });
                if (error === null) props.onReplying(false);
                return error;
              }}
              onCancel={() => props.onReplying(false)}
            />
          </Show>
        </div>
      </Show>
    </div>
  );
}

/** A new comment being written on one line of the PR. */
export function PrDraftCard(props: {
  draftKey: string;
  line: number;
  viewer: PrUser;
  onSubmit: (body: string) => Promise<string | null>;
  onCancel: () => void;
}): JSX.Element {
  return (
    <div class="weavie-pr-card weavie-pr-card-draft">
      <div class="weavie-pr-card-foot">
        <Avatar user={props.viewer} />
        <PrCommentComposer
          draftKey={props.draftKey}
          initial=""
          placeholder={`Comment on line ${props.line}…`}
          submitLabel="Comment"
          onSubmit={props.onSubmit}
          onCancel={props.onCancel}
        />
      </div>
    </div>
  );
}
