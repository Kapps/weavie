import { createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";

interface ComposerHandle {
  submit(): void;
  cancel(): void;
}

// The composer holding focus, so the submit/cancel commands act on exactly the box being typed in.
let focused: ComposerHandle | null = null;

/** Runs the focused composer's submit (or cancel); false when no comment box has focus. */
export function actOnFocusedComposer(action: keyof ComposerHandle): boolean {
  if (focused === null) return false;
  focused[action]();
  return true;
}

/** A comment box shared by new comments, replies, and edits. A failed save keeps the draft and shows why. */
export function PrCommentComposer(props: {
  initial: string;
  placeholder: string;
  submitLabel: string;
  onSubmit: (body: string) => Promise<string | null>;
  onCancel: () => void;
}): JSX.Element {
  const [error, setError] = createSignal<string | null>(null);
  const [busy, setBusy] = createSignal(false);
  let input!: HTMLTextAreaElement;
  const grow = (): void => {
    input.style.height = "auto";
    input.style.height = `${input.scrollHeight}px`;
  };
  const submit = async (): Promise<void> => {
    const body = input.value.trim();
    if (body === "" || busy()) return;
    setBusy(true);
    setError(null);
    const failure = await props.onSubmit(body);
    setBusy(false);
    setError(failure);
    if (failure !== null) input.focus();
  };
  const handle: ComposerHandle = { submit: () => void submit(), cancel: () => props.onCancel() };
  onMount(() => {
    grow();
    // A frame later: a palette that launched the comment command restores its prior focus as it closes.
    requestAnimationFrame(() => {
      input.focus();
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
        value={props.initial}
        readOnly={busy()}
        onInput={grow}
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
          title={`Cancel${keyHint(CommandIds.prCancelComment)}`}
          onClick={() => props.onCancel()}
        >
          Cancel
        </button>
        <button
          type="button"
          class="weavie-pr-button weavie-pr-button-primary"
          disabled={busy()}
          title={`${props.submitLabel}${keyHint(CommandIds.prSubmitComment)}`}
          onClick={() => void submit()}
        >
          {busy() ? "Saving…" : props.submitLabel}
        </button>
      </div>
    </div>
  );
}
