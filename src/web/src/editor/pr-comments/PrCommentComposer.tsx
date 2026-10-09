import { createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";

interface ComposerHandle {
  submit(): void;
  cancel(): void;
}

// The composer holding focus, so the submit/cancel commands act on exactly the box being typed in.
let focused: ComposerHandle | null = null;

// Unsent text per comment box, so cancelling (or a stray Escape) never throws away what was typed.
const drafts = new Map<string, string>();

/** Runs the focused composer's submit (or cancel); false when no comment box has focus. */
export function actOnFocusedComposer(action: keyof ComposerHandle): boolean {
  if (focused === null) return false;
  focused[action]();
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
  onCancel: () => void;
}): JSX.Element {
  const [error, setError] = createSignal<string | null>(null);
  const [busy, setBusy] = createSignal(false);
  const [text, setText] = createSignal(drafts.get(props.draftKey) ?? props.initial);
  let input!: HTMLTextAreaElement;
  const grow = (): void => {
    input.style.height = "auto";
    input.style.height = `${input.scrollHeight}px`;
  };
  const submit = async (): Promise<void> => {
    const body = text().trim();
    if (body === "" || busy()) return;
    setBusy(true);
    setError(null);
    const failure = await props.onSubmit(body);
    setBusy(false);
    setError(failure === null ? null : sentence(failure));
    if (failure === null) drafts.delete(props.draftKey);
    else input.focus({ preventScroll: true });
  };
  const handle: ComposerHandle = { submit: () => void submit(), cancel: () => props.onCancel() };
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
  onCleanup(() => {
    if (focused === handle) focused = null;
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
        onFocus={() => {
          focused = handle;
        }}
        onBlur={() => {
          if (focused === handle) focused = null;
        }}
      />
      <Show when={error()}>
        {(message) => <div class="weavie-pr-composer-error">{message()}</div>}
      </Show>
      <div class="weavie-pr-composer-actions">
        <button
          type="button"
          class="weavie-pr-button"
          title={`Cancel${keyHint(CommandIds.prCancelComment)} — keeps your draft`}
          onClick={() => props.onCancel()}
        >
          Cancel
        </button>
        <button
          type="button"
          class="weavie-pr-button weavie-pr-button-primary"
          disabled={busy() || text().trim() === ""}
          title={`${props.submitLabel}${keyHint(CommandIds.prSubmitComment)}`}
          onClick={() => void submit()}
        >
          {busy() ? "Saving…" : props.submitLabel}
        </button>
      </div>
    </div>
  );
}
