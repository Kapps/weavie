import { For, type JSX, Show, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { activeBackendId, requestBranches } from "../bridge";

// Prompt for "Diff Against…": name the ref to review the working tree against — a typeahead over the active
// session's local branches, or any typed commit-ish (a tag, a SHA, HEAD~2). Enter diffs, Esc cancels.
export function DiffAgainstPrompt(props: {
  onPick: (ref: string) => void;
  onCancel: () => void;
}): JSX.Element {
  const [ref, setRef] = createSignal("");
  const [branches, setBranches] = createSignal<string[]>([]);
  const [highlight, setHighlight] = createSignal(-1);

  void requestBranches(activeBackendId()).then(setBranches);

  const trimmed = (): string => ref().trim();
  // Existing branches containing the typed text (case-insensitive), minus an exact full match; capped.
  const suggestions = (): string[] => {
    const q = trimmed().toLowerCase();
    if (q.length === 0) {
      return [];
    }
    return branches()
      .filter((b) => b.toLowerCase().includes(q) && b !== trimmed())
      .slice(0, 8);
  };

  const pick = (name: string): void => {
    if (name.length > 0) {
      props.onPick(name);
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    const list = suggestions();
    if (event.key === "ArrowDown" && list.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((h) => (h + 1) % list.length);
    } else if (event.key === "ArrowUp" && list.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((h) => (h <= 0 ? list.length - 1 : h - 1));
    } else if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      const picked = highlight() >= 0 && highlight() < list.length ? list[highlight()] : undefined;
      pick(picked ?? trimmed());
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
        <div
          class="confirm-dialog session-prompt"
          role="dialog"
          aria-modal="true"
          aria-labelledby="diff-against-title"
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title" id="diff-against-title">
            Diff against
          </div>
          <div class="confirm-body">
            Review the working tree's changes against a branch, tag, or commit (from its merge-base
            with HEAD).
          </div>
          <div class="session-prompt-field">
            <input
              class="session-prompt-input"
              type="text"
              placeholder="branch, tag, or commit (e.g. main, HEAD~2)"
              role="combobox"
              aria-label="Ref to diff against"
              aria-autocomplete="list"
              aria-expanded={suggestions().length > 0}
              aria-controls={suggestions().length > 0 ? "diff-against-suggestions" : undefined}
              aria-activedescendant={
                highlight() >= 0 ? `diff-against-opt-${highlight()}` : undefined
              }
              spellcheck={false}
              autocomplete="off"
              value={ref()}
              onInput={(event) => {
                setRef(event.currentTarget.value);
                setHighlight(-1);
              }}
              ref={(el) => {
                queueMicrotask(() => el.focus());
              }}
            />
            <Show when={suggestions().length > 0}>
              <div
                class="session-prompt-suggestions"
                id="diff-against-suggestions"
                role="listbox"
                aria-label="Matching branches"
              >
                <For each={suggestions()}>
                  {(name, i) => (
                    <div
                      class="session-prompt-suggestion"
                      role="option"
                      tabindex={-1}
                      id={`diff-against-opt-${i()}`}
                      aria-selected={i() === highlight()}
                      classList={{ active: i() === highlight() }}
                      // pointerdown (not click) so picking a suggestion isn't lost to the input's blur, and
                      // preventDefault keeps focus in the field.
                      onPointerDown={(event) => {
                        event.preventDefault();
                        pick(name);
                      }}
                    >
                      {name}
                    </div>
                  )}
                </For>
              </div>
            </Show>
          </div>
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
              onClick={() => pick(trimmed())}
              title={`Diff against ${trimmed().length > 0 ? trimmed() : "the typed ref"} (Enter)`}
            >
              <span class="session-prompt-btn-label">Diff</span>
              <span class="session-prompt-btn-key">Enter</span>
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
