import { ChevronRight, ChevronUp, MessageSquare, Pencil } from "lucide-solid";
import { createSignal, For, Index, type JSX, Show } from "solid-js";
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

// A stable hue per login, so each participant reads as the same person across threads.
function Avatar(props: { login: string }): JSX.Element {
  const hue = () =>
    [...props.login].reduce((sum, char) => (sum * 31 + char.charCodeAt(0)) % 360, 7);
  return (
    <span
      class="weavie-pr-avatar"
      style={{ background: `hsl(${hue()} 55% 48%)` }}
      aria-hidden="true"
    >
      {props.login.charAt(0).toUpperCase()}
    </span>
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
      <Avatar login={props.comment.author} />
      <div class="weavie-pr-comment-main">
        <div class="weavie-pr-comment-meta">
          <span class="weavie-pr-author">{props.comment.author}</span>
          <span class="weavie-pr-time" title={new Date(time() * 1000).toLocaleString()}>
            {relativeTime(time(), Date.now() / 1000)}
            {seconds(props.comment.updatedAt) > time() ? " · edited" : ""}
          </span>
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

/** One review thread: its comments, then a reply box. Outdated threads start collapsed to a one-line summary. */
export function PrThreadCard(props: {
  placement: ThreadPlacement;
  session: ClientSession;
  number: number;
  viewer: string;
  replying: boolean;
  onReplying: (open: boolean) => void;
}): JSX.Element {
  const [collapsed, setCollapsed] = createSignal(props.placement.note === "outdated");
  const thread = () => props.placement.thread;
  const note = () => (props.placement.note === null ? null : NOTES[props.placement.note]);
  const participants = () => [...new Set(thread().comments.map((comment) => comment.author))];
  return (
    <div class="weavie-pr-card" classList={{ "weavie-pr-card-collapsed": collapsed() }}>
      <Show
        when={!collapsed()}
        fallback={
          <button
            type="button"
            class="weavie-pr-summary"
            aria-expanded="false"
            onClick={() => setCollapsed(false)}
          >
            <ChevronRight size={14} />
            <span class="weavie-pr-avatars">
              <For each={participants()}>{(login) => <Avatar login={login} />}</For>
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
          onClick={() => setCollapsed(true)}
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
        {/* Index, not For: each push rebuilds comment objects, which must not remount an open edit box. */}
        <Index each={thread().comments}>
          {(comment) => (
            <CommentItem comment={comment()} session={props.session} number={props.number} />
          )}
        </Index>
        <div class="weavie-pr-card-foot">
          <Avatar login={props.viewer} />
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
  viewer: string;
  onSubmit: (body: string) => Promise<string | null>;
  onCancel: () => void;
}): JSX.Element {
  return (
    <div class="weavie-pr-card weavie-pr-card-draft">
      <div class="weavie-pr-card-foot">
        <Avatar login={props.viewer} />
        <PrCommentComposer
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
