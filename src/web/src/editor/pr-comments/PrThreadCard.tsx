import { ChevronRight, ChevronUp, MessageSquare, WandSparkles, Wrench } from "lucide-solid";
import { createSignal, For, type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import { type FixItem, fixLine, fixWith } from "../FixPrompt";
import type { ThreadPlacement } from "./anchor-map";
import { PrCommentComposer } from "./PrCommentComposer";
import { Avatar, CommentItem } from "./PrCommentItem";
import { type PrComment, type PrUser, sendPrComment } from "./pr-comments-store";

const NOTES = {
  outdated: ["Outdated", "The PR's code has changed since this comment was made."],
  removed: ["Removed line", "This comment is on a line the PR removes."],
  changed: ["Changed locally", "Your copy no longer has the line this comment is on."],
} as const;

// A thread longer than this shows its opening comment and latest replies, folding the middle.
const FOLD_AFTER = 4;
const FOLD_KEEP_LATEST = 2;

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
  fix: () => FixItem;
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
        <span class="weavie-pr-thread-actions">
          <button
            type="button"
            class="weavie-pr-icon-button"
            title={`Fix${keyHint(CommandIds.reviewHintsFix)}`}
            aria-label="Fix"
            onClick={() => fixLine(props.session, props.fix(), "")}
          >
            <Wrench size={13} />
          </button>
          <button
            type="button"
            class="weavie-pr-icon-button"
            title={`Fix with…${keyHint(CommandIds.reviewHintsFixWith)}`}
            aria-label="Fix with…"
            onClick={() => fixWith(props.session, props.fix())}
          >
            <WandSparkles size={13} />
          </button>
          <button
            type="button"
            class="weavie-pr-icon-button"
            title="Collapse thread"
            aria-label="Collapse thread"
            aria-expanded="true"
            onClick={() => props.onCollapsed(true)}
          >
            <ChevronUp size={14} />
          </button>
        </span>
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
