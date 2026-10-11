import { createEffect, createSignal, type JSX, on, onCleanup, onMount, Show } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";

/** What the comment-box commands do to the focused box. */
export interface ComposerHandle {
  submit: () => void;
  /** The box's second way to send; null when it has none, so the command passes the key on. */
  alternate: (() => void) | null;
  cancel: () => void;
}

// The composer holding focus, so the submit/cancel commands act on exactly the box being typed in.
let focused: ComposerHandle | null = null;

/** Focus handlers that make `handle` the target of the comment-box commands while its input holds focus. */
export function composerFocus(handle: ComposerHandle): { onFocus: () => void; onBlur: () => void } {
  onCleanup(() => {
    if (focused === handle) focused = null;
  });
  return {
    onFocus: () => {
      focused = handle;
    },
    onBlur: () => {
      if (focused === handle) focused = null;
    },
  };
}

/** A composer's second way to send, beside its primary one (e.g. post now instead of queueing). */
export interface ComposerAlternate {
  label: string;
  onSubmit: (body: string) => Promise<string | null>;
}

// Unsent text per comment box, so cancelling (or a stray Escape) never throws away what was typed.
const drafts = new Map<string, string>();
// Bumped by a prefill, so a box that's already open shows what was added to its draft.
const [prefilled, setPrefilled] = createSignal(0);

/** Adds `text` to a comment box's draft (after any unsent text), shown whether or not the box is open. */
export function prefillComposer(draftKey: string, text: string): void {
  if (text === "") return;
  const unsent = drafts.get(draftKey);
  drafts.set(draftKey, unsent === undefined ? text : `${unsent}\n\n${text}`);
  setPrefilled((count) => count + 1);
}

/** Runs the focused composer's submit (or cancel); false when no comment box has focus. */
export function actOnFocusedComposer(action: keyof ComposerHandle): boolean {
  const run = focused?.[action] ?? null;
  if (run === null) return false;
  run();
  return true;
}

const sentence = (text: string): string => text.charAt(0).toUpperCase() + text.slice(1);

/** A comment box shared by new comments, replies, and edits. A failed save keeps the draft and shows why. */
export function PrCommentComposer(props: {
  /** Identifies this box's unsent draft across closes and reopens. */
  draftKey: string;
  initial: string;
  placeholder: string;
  submitLabel: string;
  onSubmit: (body: string) => Promise<string | null>;
  alternate: ComposerAlternate | null;
  /** Context beside the actions, e.g. how many comments the pending review holds. */
  note: string;
  onCancel: () => void;
}): JSX.Element {
  const [error, setError] = createSignal<string | null>(null);
  const [busy, setBusy] = createSignal(false);
  const [text, setText] = createSignal(drafts.get(props.draftKey) ?? props.initial);
  let input!: HTMLTextAreaElement;
  createEffect(
    on(
      prefilled,
      () => {
        const draft = drafts.get(props.draftKey);
        if (draft === undefined || draft === text()) return;
        setText(draft);
        // The bound value lands after this effect, so set it now for grow() to measure.
        input.value = draft;
        grow();
        input.focus({ preventScroll: true });
        input.setSelectionRange(draft.length, draft.length);
      },
      { defer: true },
    ),
  );
  const grow = (): void => {
    input.style.height = "auto";
    input.style.height = `${input.scrollHeight}px`;
  };
  const send = async (action: (body: string) => Promise<string | null>): Promise<void> => {
    const body = text().trim();
    if (body === "" || busy()) return;
    setBusy(true);
    setError(null);
    const failure = await action(body);
    setBusy(false);
    setError(failure === null ? null : sentence(failure));
    if (failure === null) drafts.delete(props.draftKey);
    else input.focus({ preventScroll: true });
  };
  const handle: ComposerHandle = {
    submit: () => void send(props.onSubmit),
    get alternate() {
      const alternate = props.alternate;
      return alternate === null ? null : () => void send(alternate.onSubmit);
    },
    cancel: () => props.onCancel(),
  };
  const focus = composerFocus(handle);
  onMount(() => {
    grow();
    // A frame later, after a launching palette restores its prior focus; never over a caret the user already
    // placed. The card layer reveals the box by scrolling the editor, so focus must not scroll anything itself.
    requestAnimationFrame(() => {
      if (document.activeElement === input) return;
      input.focus({ preventScroll: true });
      input.setSelectionRange(input.value.length, input.value.length);
    });
  });
  return (
    <div class="weavie-pr-composer">
      <textarea
        ref={input}
        data-pr-comment-input
        class="weavie-pr-composer-input"
        rows={2}
        placeholder={props.placeholder}
        value={text()}
        readOnly={busy()}
        onInput={() => {
          setText(input.value);
          if (input.value === props.initial) drafts.delete(props.draftKey);
          else drafts.set(props.draftKey, input.value);
          grow();
        }}
        onFocus={focus.onFocus}
        onBlur={focus.onBlur}
      />
      <Show when={error()}>
        {(message) => <div class="weavie-pr-composer-error">{message()}</div>}
      </Show>
      <div class="weavie-pr-composer-actions">
        <Show when={props.note !== ""}>
          <span class="weavie-pr-composer-note">{props.note}</span>
        </Show>
        <button
          type="button"
          class="weavie-pr-button"
          title={`Cancel${keyHint(CommandIds.prCancelComment)} — keeps your draft`}
          onClick={() => props.onCancel()}
        >
          Cancel
        </button>
        <Show when={props.alternate}>
          {(alternate) => (
            <button
              type="button"
              class="weavie-pr-button"
              disabled={busy() || text().trim() === ""}
              title={`${alternate().label}${keyHint(CommandIds.prPostCommentNow)}`}
              onClick={() => void send(alternate().onSubmit)}
            >
              {alternate().label}
            </button>
          )}
        </Show>
        <button
          type="button"
          class="weavie-pr-button weavie-pr-button-primary"
          disabled={busy() || text().trim() === ""}
          title={`${props.submitLabel}${keyHint(CommandIds.prSubmitComment)}`}
          onClick={() => handle.submit()}
        >
          {busy() ? "Saving…" : props.submitLabel}
        </button>
      </div>
    </div>
  );
}
