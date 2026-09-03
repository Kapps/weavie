import { RefreshCw } from "lucide-solid";
import { createEffect, createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import { backendPhase, type EncodedImageAttachment, requestBranchPreview } from "../bridge";
import {
  type BranchPreviewState,
  NewSessionBranchPreview,
} from "../chrome/new-session-branch-preview";
import { keyHint } from "../commands/key-hint";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";

export interface NewSessionBranchActions {
  flush: () => void;
  reset: () => void;
  resolve: () => Promise<string>;
}

/** Editable, cancellable branch-name preview for the shared Sessions composer. */
export function NewSessionBranchField(props: {
  active: boolean;
  backendId: string;
  attachments: readonly EncodedImageAttachment[];
  inputReady: boolean;
  prompt: string;
  onChange: (state: BranchPreviewState) => void;
  register: (actions: NewSessionBranchActions) => void;
}): JSX.Element {
  const [state, setState] = createSignal<BranchPreviewState>({
    branch: "",
    error: null,
    manual: false,
    status: "idle",
  });
  const online = (): boolean => backendPhase(props.backendId) === "online";
  const preview = new NewSessionBranchPreview(
    (context, userInitiated, signal) =>
      requestBranchPreview(
        context.backendId,
        context.prompt,
        context.attachments,
        userInitiated,
        signal,
      ),
    (next) => {
      setState(next);
      props.onChange(next);
    },
  );
  props.register({
    flush: () => preview.flush(),
    reset: () => preview.reset(),
    resolve: () => preview.resolve(),
  });

  onMount(() =>
    onCleanup(
      registerCommand(CommandIds.resuggestBranch, () => {
        if (!props.active || !props.inputReady || !online()) {
          return false;
        }
        preview.refresh();
        return true;
      }),
    ),
  );

  // Switching surfaces is not an edit: the suggestion for this prompt outlives leaving the composer.
  createEffect(() => {
    const prompt = props.prompt.trim();
    preview.update(
      props.inputReady && online()
        ? {
            backendId: props.backendId,
            prompt,
            attachments: props.attachments,
          }
        : null,
    );
  });

  onCleanup(() => preview.cancel());

  const placeholder = (): string => {
    switch (state().status) {
      case "loading":
        return "Suggesting…";
      case "needsDetail":
        return "Say more, or type a name";
      case "error":
        return "Preview unavailable";
      default:
        return "Name from prompt";
    }
  };

  return (
    <div class="session-composer-branch">
      <label for="new-session-branch">Branch</label>
      <input
        id="new-session-branch"
        type="text"
        aria-label="Branch for the new session"
        autocapitalize="none"
        autocomplete="off"
        spellcheck={false}
        placeholder={placeholder()}
        value={state().branch}
        onInput={(event) => preview.edit(event.currentTarget.value)}
        onFocus={() => preview.claim()}
        onBlur={() => preview.release()}
      />
      <button
        type="button"
        class="session-composer-resuggest"
        aria-label="Suggest a branch name again"
        title={`Suggest again${keyHint(CommandIds.resuggestBranch)}`}
        disabled={!props.inputReady || !online() || state().status === "loading"}
        onClick={() => preview.refresh()}
      >
        <RefreshCw size={16} aria-hidden="true" />
      </button>
      <Show when={state().status === "error"}>
        <small role="alert">
          Branch suggestion failed: {state().error} Type a branch to continue.
        </small>
      </Show>
    </div>
  );
}
