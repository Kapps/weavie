import { type JSX, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";

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
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      submit();
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onCancel();
    }
  };
  onMount(() => window.addEventListener("keydown", onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onCancel()}>
        <div class="confirm-dialog" onPointerDown={(event) => event.stopPropagation()}>
          <div class="confirm-title">Save as</div>
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
          <div class="session-prompt-actions">
            <button
              type="button"
              class="session-prompt-btn"
              onClick={() => props.onCancel()}
              title="Cancel (Esc)"
            >
              <span class="session-prompt-btn-label">Cancel</span>
              <span class="session-prompt-btn-key">Esc</span>
            </button>
            <button
              type="button"
              class="session-prompt-btn session-prompt-btn-primary"
              onClick={() => submit()}
              title="Save (Enter)"
            >
              <span class="session-prompt-btn-label">Save</span>
              <span class="session-prompt-btn-key">Enter</span>
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
