import type { JSX } from "solid-js";
import { ModalShell, modalSubmitKeys, PromptActions, PromptButton } from "../chrome/ModalShell";

/**
 * In-app "Save as" name prompt for scratch buffers on a browser-served host (headless / remote), where there's
 * no native Save-As dialog. Collects a workspace-relative path; the host resolves it under the workspace root.
 * Enter saves, Escape cancels (capture-phase so the global keybinding resolver never sees them).
 */
export function SaveAsPrompt(props: {
  suggestedName: string;
  onSave: (name: string) => void;
  onCancel: () => void;
}): JSX.Element {
  let input!: HTMLInputElement;
  const submit = (): void => {
    const name = input.value.trim();
    if (name !== "") {
      props.onSave(name);
    }
  };
  const onKeyDown = modalSubmitKeys(submit, props.onCancel);
  return (
    <ModalShell labelledBy="save-as-title" onDismiss={props.onCancel} onKeyDown={onKeyDown}>
      <div class="confirm-title" id="save-as-title">
        Save as
      </div>
      <div class="confirm-body">Name this file, relative to the workspace root.</div>
      <input
        class="session-prompt-input"
        type="text"
        spellcheck={false}
        autocomplete="off"
        value={props.suggestedName}
        ref={(el) => {
          input = el;
          queueMicrotask(() => el.select());
        }}
      />
      <PromptActions>
        <PromptButton label="Cancel" shortcut="Esc" title="Cancel (Esc)" onClick={props.onCancel} />
        <PromptButton label="Save" shortcut="Enter" title="Save (Enter)" onClick={submit} primary />
      </PromptActions>
    </ModalShell>
  );
}
