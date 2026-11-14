import type { JSX } from "solid-js";
import { ModalShell, modalSubmitKeys, PromptButton } from "../chrome/ModalShell";

/**
 * Collects the instruction for a revision of the selected lines. The model does the rewriting; deciding what to
 * ask for stays with the user, so nothing is revised without an instruction they typed.
 */
export function RevisePrompt(props: {
  lineCount: number;
  onRevise: (instruction: string) => void;
  onCancel: () => void;
}): JSX.Element {
  let input!: HTMLInputElement;
  const submit = (): void => {
    const instruction = input.value.trim();
    if (instruction !== "") {
      props.onRevise(instruction);
    }
  };
  const onKeyDown = modalSubmitKeys(submit, props.onCancel);
  return (
    <ModalShell
      title="Revise selection"
      actions={
        <>
          <PromptButton
            label="Cancel"
            shortcut="Esc"
            title="Cancel (Esc)"
            onClick={props.onCancel}
          />
          <PromptButton
            label="Revise"
            shortcut="Enter"
            title="Revise (Enter)"
            onClick={submit}
            primary
          />
        </>
      }
      onDismiss={props.onCancel}
      onKeyDown={onKeyDown}
    >
      <div class="confirm-body">
        What should happen to{" "}
        {props.lineCount === 1 ? "this line" : `these ${props.lineCount} lines`}?
      </div>
      <input
        class="session-prompt-input ui-field"
        type="text"
        spellcheck={false}
        autocomplete="off"
        placeholder="Shorten this comment to at most two lines"
        ref={(el) => {
          input = el;
          queueMicrotask(() => el.focus());
        }}
      />
    </ModalShell>
  );
}
