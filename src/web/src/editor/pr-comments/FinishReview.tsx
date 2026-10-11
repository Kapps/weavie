import { ChevronDown } from "lucide-solid";
import { createEffect, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { revealFileIn } from "../../files/reveal";
import { notify } from "../../notify/notify";
import { ConfirmDialog } from "../ConfirmDialog";
import { composerFocus } from "./PrCommentComposer";
import {
  type PrCommentSet,
  type PrVerdict,
  pendingDrafts,
  prCommentsFor,
  sendPrComment,
} from "./pr-comments-store";
import "./finish-review.css";
import "./pr-comments.css";

const VERDICTS: { verdict: PrVerdict; label: string; detail: string }[] = [
  { verdict: "comment", label: "Comment", detail: "General feedback without explicit approval." },
  { verdict: "approve", label: "Approve", detail: "Ready to merge." },
  {
    verdict: "requestChanges",
    label: "Request changes",
    detail: "Must be addressed before merging.",
  },
];

type Request = "open" | "discard";

const [request, setRequest] = createSignal<{ session: ClientSession; kind: Request } | null>(null);

// Unsent summaries and picked verdicts per PR (by URL), so closing the panel never throws away what was chosen.
const summaries = new Map<string, string>();
const verdicts = new Map<string, PrVerdict>();

// The session whose panel is open. Module state, so the panel survives its control moving between the overview and
// the review header as the user scrolls.
const [openFor, setOpenFor] = createSignal<ClientSession | null>(null);

/** Shows the review that hosts `session`'s Finish review control, then opens its panel (or its discard confirm). */
export async function requestFinishReview(session: ClientSession, kind: Request): Promise<void> {
  if ((await runCommandWithFeedback(CommandIds.reviewOverview)).ok) setRequest({ session, kind });
}

/**
 * The session's review-submit control: a header button carrying the pending count, opening a panel to write a
 * summary, pick a verdict, and submit or discard the pending review. Self-contained, so any surface can host it.
 */
export function FinishReview(props: { session: ClientSession }): JSX.Element {
  const open = (): boolean => openFor() === props.session;
  const setOpen = (shown: boolean): void => {
    if (shown) setOpenFor(props.session);
    else if (open()) setOpenFor(null);
  };
  const [confirming, setConfirming] = createSignal(false);
  const set = () => prCommentsFor(props.session)?.set ?? null;
  createEffect(() => {
    const asked = request();
    if (asked?.session !== props.session || set() === null) return;
    setRequest(null);
    setOpen(true);
    setConfirming(asked.kind === "discard" && set()?.pending != null);
  });
  let root!: HTMLDivElement;
  const onPointerDown = (event: PointerEvent): void => {
    if (open() && !confirming() && !root.contains(event.target as Node)) setOpen(false);
  };
  document.addEventListener("pointerdown", onPointerDown, true);
  onCleanup(() => document.removeEventListener("pointerdown", onPointerDown, true));
  return (
    <Show when={set()}>
      {(active) => {
        const count = () => pendingDrafts(active()).length;
        return (
          <div class="finish-review" ref={root}>
            <button
              type="button"
              class="finish-review-button"
              classList={{ "finish-review-has": count() > 0, "finish-review-open": open() }}
              aria-expanded={open()}
              title={`Finish your review of PR #${active().number}${keyHint(CommandIds.prFinishReview)}`}
              onClick={() => setOpen(!open())}
            >
              {count() > 0 ? "Finish review" : "Review"}
              <Show when={count() > 0}>
                <span class="finish-review-count">{count()}</span>
              </Show>
              <ChevronDown size="1em" />
            </button>
            <Show when={open()}>
              <ReviewPanel
                session={props.session}
                set={active()}
                onClose={() => setOpen(false)}
                onDiscard={() => setConfirming(true)}
              />
            </Show>
            <Show when={confirming()}>
              <ConfirmDialog
                title="Discard your pending review?"
                body={`Your ${count()} pending comment${count() === 1 ? "" : "s"} on PR #${active().number} will be deleted.`}
                confirmLabel="Discard"
                cancelLabel="Keep"
                onConfirm={() => {
                  setConfirming(false);
                  void sendPrComment(props.session, "discardReview", {
                    number: active().number,
                  }).then((error) => {
                    if (error !== null)
                      notify("error", `Couldn't discard the review: ${error}`, "pr-comments");
                  });
                }}
                onCancel={() => setConfirming(false)}
              />
            </Show>
          </div>
        );
      }}
    </Show>
  );
}

function ReviewPanel(props: {
  session: ClientSession;
  set: PrCommentSet;
  onClose: () => void;
  onDiscard: () => void;
}): JSX.Element {
  const number = props.set.number;
  const key = props.set.url;
  const [summary, setSummary] = createSignal(summaries.get(key) ?? props.set.pending?.body ?? "");
  const [verdict, setVerdict] = createSignal<PrVerdict>(verdicts.get(key) ?? "comment");
  const [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);
  const submit = async (): Promise<void> => {
    if (busy()) return;
    setBusy(true);
    setError(null);
    const failure = await sendPrComment(props.session, "submitReview", {
      number,
      headSha: props.set.headSha,
      verdict: props.set.viewerIsAuthor ? "comment" : verdict(),
      body: summary().trim(),
    });
    setBusy(false);
    if (failure !== null) {
      setError(`Couldn't submit: ${failure}`);
      return;
    }
    summaries.delete(key);
    verdicts.delete(key);
    props.onClose();
  };
  const focus = composerFocus({
    submit: () => void submit(),
    alternate: null,
    cancel: () => props.onClose(),
  });
  return (
    <div class="finish-review-panel" role="dialog" aria-label="Finish your review">
      <div class="finish-review-title">
        Finish your review
        <span>{props.set.viewerIsAuthor ? `your PR #${number}` : `PR #${number}`}</span>
      </div>
      <textarea
        // A frame later, after the review the command opened (or the palette) has settled focus.
        ref={(el) => requestAnimationFrame(() => el.focus({ preventScroll: true }))}
        data-pr-comment-input
        class="weavie-pr-composer-input"
        rows={3}
        placeholder="Leave a summary (optional)"
        value={summary()}
        readOnly={busy()}
        onInput={(event) => {
          setSummary(event.currentTarget.value);
          summaries.set(key, event.currentTarget.value);
        }}
        onFocus={focus.onFocus}
        onBlur={focus.onBlur}
      />
      <Show
        when={!props.set.viewerIsAuthor}
        fallback={
          <div class="finish-review-note">
            Your own pull request — this posts your comments as one review.
          </div>
        }
      >
        <div class="finish-review-verdicts" role="radiogroup">
          <For each={VERDICTS}>
            {(option) => (
              <label
                class={`finish-review-verdict finish-review-${option.verdict}`}
                classList={{ "finish-review-selected": verdict() === option.verdict }}
              >
                <input
                  type="radio"
                  name={`verdict-${number}`}
                  checked={verdict() === option.verdict}
                  onChange={() => {
                    verdicts.set(key, option.verdict);
                    setVerdict(option.verdict);
                  }}
                />
                <span>
                  <b>{option.label}</b>
                  <span class="finish-review-detail">{option.detail}</span>
                </span>
              </label>
            )}
          </For>
        </div>
      </Show>
      <div class="finish-review-drafts">
        <For each={pendingDrafts(props.set)}>
          {({ thread, comment }) => (
            <button
              type="button"
              class="finish-review-draft"
              title="Go to this comment"
              onClick={() => revealFileIn(props.session, thread.path, thread.line, false)}
            >
              <span class="finish-review-where">
                {thread.path.split(/[\\/]/).pop()}:{thread.line}
              </span>
              <span class="finish-review-body">
                {thread.comments[0] === comment ? "" : "↳ "}
                {comment.body.split("\n")[0]}
              </span>
            </button>
          )}
        </For>
      </div>
      <Show when={error()}>
        {(message) => <div class="weavie-pr-composer-error">{message()}</div>}
      </Show>
      <div class="weavie-pr-composer-actions">
        <Show when={props.set.pending !== null}>
          <button
            type="button"
            class="weavie-pr-button finish-review-discard"
            title={`Discard pending review${keyHint(CommandIds.prDiscardReview)}`}
            onClick={() => props.onDiscard()}
          >
            Discard review…
          </button>
        </Show>
        <button
          type="button"
          class="weavie-pr-button"
          title={`Cancel${keyHint(CommandIds.prCancelComment)}`}
          onClick={() => props.onClose()}
        >
          Cancel
        </button>
        <button
          type="button"
          class="weavie-pr-button weavie-pr-button-primary"
          disabled={busy()}
          title={`Submit review${keyHint(CommandIds.prSubmitComment)}`}
          onClick={() => void submit()}
        >
          {busy() ? "Submitting…" : "Submit review"}
        </button>
      </div>
    </div>
  );
}
