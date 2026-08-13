import { createEffect, createSignal, type JSX, onCleanup, Show } from "solid-js";
import { backendPhase, requestBranchPreview } from "../bridge";
import {
  type BranchPreviewState,
  NewSessionBranchPreview,
} from "../chrome/new-session-branch-preview";

export interface NewSessionBranchActions {
  cancel: () => void;
  reset: () => void;
}

/** Editable, cancellable branch-name preview for the shared Sessions composer. */
export function NewSessionBranchField(props: {
  active: boolean;
  backendId: string;
  hasInput: boolean;
  prompt: string;
  providerId: "claude" | "codex";
  onChange: (state: BranchPreviewState) => void;
  register: (actions: NewSessionBranchActions) => void;
}): JSX.Element {
  const [state, setState] = createSignal<BranchPreviewState>({
    branch: "",
    error: null,
    manual: false,
    status: "idle",
  });
  const preview = new NewSessionBranchPreview(
    (context, signal) =>
      requestBranchPreview(context.backendId, context.prompt, context.providerId, signal),
    (next) => {
      setState(next);
      props.onChange(next);
    },
  );
  props.register({ cancel: () => preview.cancel(), reset: () => preview.reset() });

  createEffect(() => {
    const prompt = props.prompt.trim();
    preview.update(
      props.active && props.hasInput && backendPhase(props.backendId) === "online"
        ? { backendId: props.backendId, prompt, providerId: props.providerId }
        : null,
    );
  });

  onCleanup(() => preview.cancel());

  const placeholder = (): string => {
    switch (state().status) {
      case "waiting":
      case "loading":
        return "Suggesting…";
      case "error":
        return "Preview unavailable";
      default:
        return "Name from prompt";
    }
  };

  return (
    <label class="session-composer-branch">
      <span>Branch</span>
      <input
        type="text"
        aria-label="Branch for the new session"
        autocapitalize="none"
        autocomplete="off"
        spellcheck={false}
        placeholder={placeholder()}
        value={state().branch}
        onInput={(event) => preview.edit(event.currentTarget.value)}
      />
      <Show when={state().status === "error"}>
        <small role="alert">
          Branch suggestion failed: {state().error} Type a branch to continue.
        </small>
      </Show>
    </label>
  );
}
