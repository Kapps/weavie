import { ChevronDown, ChevronRight, MessageSquare, Pencil } from "lucide-solid";
import { createSignal, Index, type JSX, Show } from "solid-js";
import { AgentMarkdown } from "../../agent/AgentMarkdown";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import { relativeTime } from "../blame-model";
import type { ThreadPlacement } from "./anchor-map";
import { PrCommentComposer } from "./PrCommentComposer";
import { type PrComment, sendPrComment } from "./pr-comments-store";

const NOTES = {
  outdated: ["Outdated", "The PR's code has changed since this comment was made."],
  removed: ["Removed line", "This comment is on a line the PR removes."],
  changed: ["Changed locally", "Your copy no longer has the line this comment is on."],
} as const;

const seconds = (iso: string): number => Date.parse(iso) / 1000;

function CommentItem(props: {
  comment: PrComment;
  session: ClientSession;
  number: number;
}): JSX.Element {
  const [editing, setEditing] = createSignal(false);
  const time = () => seconds(props.comment.createdAt);
  return (
    <div class="weavie-pr-comment">
      <div class="weavie-pr-avatar" aria-hidden="true">
        {props.comment.author.charAt(0).toUpperCase()}
      </div>
      <div class="weavie-pr-comment-main">
        <div class="weavie-pr-comment-meta">
          <span class="weavie-pr-author">{props.comment.author}</span>
          <span class="weavie-pr-time" title={new Date(time() * 1000).toLocaleString()}>
            {relativeTime(time(), Date.now() / 1000)}
          </span>
          <Show when={seconds(props.comment.updatedAt) > time()}>
            <span class="weavie-pr-time">· edited</span>
          </Show>
          <Show when={props.comment.mine && !editing()}>
            <button
              type="button"
              class="weavie-pr-icon-button"
              title="Edit comment"
              aria-label="Edit comment"
              onClick={() => setEditing(true)}
            >
              <Pencil size={13} />
            </button>
          </Show>
        </div>
        <Show
          when={editing()}
          fallback={
            <div class="weavie-pr-comment-body">
              <AgentMarkdown
                cacheKey={props.comment}
                content={props.comment.body}
                renderMermaid={false}
                session={props.session}
              />
            </div>
          }
        >
          <PrCommentComposer
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

/** One review thread: its comments, then a reply box. Outdated threads start collapsed. */
export function PrThreadCard(props: {
  placement: ThreadPlacement;
  session: ClientSession;
  number: number;
  replying: boolean;
  onReplying: (open: boolean) => void;
}): JSX.Element {
  const [collapsed, setCollapsed] = createSignal(props.placement.note === "outdated");
  const thread = () => props.placement.thread;
  const note = () => (props.placement.note === null ? null : NOTES[props.placement.note]);
  const count = () => thread().comments.length;
  return (
    <div class="weavie-pr-card" classList={{ "weavie-pr-card-collapsed": collapsed() }}>
      <button
        type="button"
        class="weavie-pr-card-head"
        aria-expanded={!collapsed()}
        onClick={() => setCollapsed((value) => !value)}
      >
        {collapsed() ? <ChevronRight size={14} /> : <ChevronDown size={14} />}
        <MessageSquare size={13} />
        <span class="weavie-pr-card-title">
          {count() === 1 ? "1 comment" : `${count()} comments`}
          <Show when={collapsed()}>
            <span class="weavie-pr-card-count">
              {` · ${thread().comments[0]?.author}: ${thread().comments[0]?.body.split("\n")[0]}`}
            </span>
          </Show>
        </span>
        <Show when={note()}>
          {(labels) => (
            <span class="weavie-pr-badge" title={labels()[1]}>
              {labels()[0]}
            </span>
          )}
        </Show>
      </button>
      <Show when={!collapsed()}>
        <Show when={props.placement.quote}>
          {(quote) => <pre class="weavie-pr-quote">{quote()}</pre>}
        </Show>
        {/* Index, not For: each push rebuilds comment objects, which must not remount an open edit box. */}
        <Index each={thread().comments}>
          {(comment) => (
            <CommentItem comment={comment()} session={props.session} number={props.number} />
          )}
        </Index>
        <div class="weavie-pr-card-foot">
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
  line: number;
  onSubmit: (body: string) => Promise<string | null>;
  onCancel: () => void;
}): JSX.Element {
  return (
    <div class="weavie-pr-card">
      <div class="weavie-pr-card-head weavie-pr-card-head-static">
        <MessageSquare size={13} />
        <span class="weavie-pr-card-title">New comment on line {props.line}</span>
      </div>
      <div class="weavie-pr-card-foot">
        <PrCommentComposer
          initial=""
          placeholder="Leave a comment…"
          submitLabel="Comment"
          onSubmit={props.onSubmit}
          onCancel={props.onCancel}
        />
      </div>
    </div>
  );
}
