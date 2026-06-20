import { type JSX, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";

// Prompt for a new worktree session: name the branch, then choose what to branch from. Keyboard-first —
// Enter branches off the active session's HEAD, Shift+Enter off main, Esc cancels — and each button carries
// its shortcut as a faint sublabel (the keyboard-first discoverability rule), so there's no separate hint
// line to repeat them. These keys are inherent to the prompt (a capture-phase listener, so the global
// keybinding resolver / editor never see them while it's up), not rebindable commands, so the labels are
// literal. Styled via CSS classes (this build's solid-js/web has no object style binding) — the shared
// .modal-backdrop sets the UI font since the Portal renders outside the .app font scope.
export function NewSessionPrompt(props: {
  onCreate: (branch: string, base: "head" | "main") => void;
  onCancel: () => void;
}): JSX.Element {
  let input!: HTMLInputElement;

  // A branch name is required — Enter/Shift+Enter no-op on an empty field (the input keeps focus) rather
  // than auto-naming, which is what produced the "branch 'session' already exists" collision.
  const submit = (base: "head" | "main"): void => {
    const branch = input.value.trim();
    if (branch.length > 0) {
      props.onCreate(branch, base);
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      submit(event.shiftKey ? "main" : "head");
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
        {/* Stop backdrop dismissal when interacting with the dialog itself. */}
        <div
          class="confirm-dialog session-prompt"
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title">New session</div>
          <div class="confirm-body">
            A session runs on its own git worktree + branch. Name the branch, then choose what to
            branch from.
          </div>
          <input
            class="session-prompt-input"
            type="text"
            placeholder="branch name"
            spellcheck={false}
            autocomplete="off"
            ref={(el) => {
              input = el;
              // Focus the field so the user can type immediately (keyboard-first).
              queueMicrotask(() => el.focus());
            }}
          />
          {/* Conventional footer: actions grouped bottom-right, primary (the Enter default) last. */}
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
              class="session-prompt-btn"
              onClick={() => submit("main")}
              title="Branch off main (Shift+Enter)"
            >
              <span class="session-prompt-btn-label">Off main</span>
              <span class="session-prompt-btn-key">Shift+Enter</span>
            </button>
            <button
              type="button"
              class="session-prompt-btn session-prompt-btn-primary"
              onClick={() => submit("head")}
              title="Branch off HEAD (Enter)"
            >
              <span class="session-prompt-btn-label">Off HEAD</span>
              <span class="session-prompt-btn-key">Enter</span>
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
