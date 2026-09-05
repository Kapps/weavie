import { createEffect, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentSlashEntry } from "../bridge";
import { dismissOnOutsideInteraction } from "../chrome/popover-dismiss";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { createListNavigation } from "../list-navigation";

// The autocomplete that opens above the composer while the draft is a slash command. `agentSlashMenuOpen` is
// set while it has entries so the composer's Enter/Escape commands stand down and this window handler drives
// selection — the same overlay pattern as the control picker. The query lives in the composer; this renders the
// filtered entries and reports the pick.
export function AgentSlashMenu(props: {
  entries: AgentSlashEntry[];
  onAccept: (entry: AgentSlashEntry, execute: boolean) => void;
  onDismiss: () => void;
}): JSX.Element {
  const nav = createListNavigation({
    count: () => props.entries.length,
    edges: "wrap",
    initialIndex: 0,
    acceptKeys: ["Enter", "Tab"],
    onAccept: (index, event) => {
      const entry = props.entries[index];
      if (entry !== undefined) {
        props.onAccept(
          entry,
          event.key === "Enter" && (entry.kind === "weavieCommand" || entry.inputHint === null),
        );
      }
    },
    onDismiss: () => props.onDismiss(),
  });
  // A new filter (each keystroke) re-homes the highlight to the top.
  createEffect(() => {
    props.entries;
    nav.setIndex(0);
  });
  createEffect(() => setContext("agentSlashMenuOpen", props.entries.length > 0));
  onCleanup(() => setContext("agentSlashMenuOpen", false));

  // Capture phase so the menu beats the composer's own history keydown while it is open. The whole composer
  // counts as inside — the query lives in its textarea — so only a click that leaves it dismisses the menu.
  createEffect(() => {
    if (props.entries.length === 0) {
      return;
    }
    window.addEventListener("keydown", nav.onKeyDown, { capture: true });
    onCleanup(() => window.removeEventListener("keydown", nav.onKeyDown, { capture: true }));
    dismissOnOutsideInteraction("[data-agent-composer]", props.onDismiss);
  });

  return (
    <Show when={props.entries.length > 0}>
      <div class="agent-slash-menu" role="listbox" aria-label="Slash commands">
        <For each={props.entries}>
          {(entry, index) => (
            <div
              {...nav.row(index())}
              class="agent-slash-option"
              role="option"
              tabindex={-1}
              aria-selected={index() === nav.index()}
              classList={{ active: index() === nav.index() }}
              onPointerDown={(event) => {
                event.preventDefault();
                props.onAccept(entry, entry.kind === "weavieCommand" || entry.inputHint === null);
              }}
            >
              <span class="agent-slash-name">
                /{entry.name}
                <Show when={entry.inputHint}>{(hint) => ` ${hint()}`}</Show>
              </span>
              <span class="agent-slash-desc">{entry.description}</span>
              <Show when={entry.commandId === null ? "" : liveKeyLabel(entry.commandId)}>
                {(key) => <span class="agent-slash-key">{key()}</span>}
              </Show>
            </div>
          )}
        </For>
      </div>
    </Show>
  );
}
