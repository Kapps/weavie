import { createEffect, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentSlashEntry } from "../bridge";
import { setContext } from "../commands/context";

// The autocomplete that opens above the composer while the draft is a slash command. `agentSlashMenuOpen` is
// set while it has entries so the composer's Enter/Escape commands stand down and this window handler drives
// selection — the same overlay pattern as the control picker. The query lives in the composer; this renders the
// filtered entries and reports the pick.
export function AgentSlashMenu(props: {
  entries: AgentSlashEntry[];
  onAccept: (entry: AgentSlashEntry) => void;
  onDismiss: () => void;
}): JSX.Element {
  const [highlight, setHighlight] = createSignal(0);
  // A new filter (each keystroke) re-homes the highlight to the top.
  createEffect(() => {
    props.entries;
    setHighlight(0);
  });
  createEffect(() => setContext("agentSlashMenuOpen", props.entries.length > 0));
  onCleanup(() => setContext("agentSlashMenuOpen", false));

  const onKeyDown = (event: KeyboardEvent): void => {
    const entries = props.entries;
    if (entries.length === 0) {
      return;
    }
    if (event.key === "ArrowDown") {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((index) => (index + 1) % entries.length);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((index) => (index <= 0 ? entries.length - 1 : index - 1));
    } else if (event.key === "Enter" || event.key === "Tab") {
      event.preventDefault();
      event.stopPropagation();
      const entry = entries[highlight()];
      if (entry !== undefined) {
        props.onAccept(entry);
      }
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onDismiss();
    }
  };

  // Capture phase so the menu beats the composer's own history keydown while it is open.
  createEffect(() => {
    if (props.entries.length === 0) {
      return;
    }
    window.addEventListener("keydown", onKeyDown, { capture: true });
    onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));
  });

  return (
    <Show when={props.entries.length > 0}>
      <div class="agent-slash-menu" role="listbox" aria-label="Slash commands">
        <For each={props.entries}>
          {(entry, index) => (
            <div
              class="agent-slash-option"
              role="option"
              tabindex={-1}
              aria-selected={index() === highlight()}
              classList={{ active: index() === highlight() }}
              onMouseEnter={() => setHighlight(index())}
              onPointerDown={(event) => {
                event.preventDefault();
                props.onAccept(entry);
              }}
            >
              <span class="agent-slash-name">/{entry.name}</span>
              <span class="agent-slash-desc">{entry.description}</span>
            </div>
          )}
        </For>
      </div>
    </Show>
  );
}
