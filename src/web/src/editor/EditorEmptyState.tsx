import { Command, FilePlus, FileSearch, FolderTree } from "lucide-solid";
import { For, type JSX } from "solid-js";
import { WeavieIcon } from "../chrome/WeavieIcon";
import { formatKey } from "../commands/keybindings";
import { dispatchCommand, findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";

// What the editor pane shows when no file is open (no tabs). Instead of a blank black void it identifies the
// app and offers the keyboard-first ways to get a file in front of you. Each row is a real button that
// dispatches the command (mouse-discoverable) AND advertises its current shortcut read from the command
// catalog (CommandInfo.keys → formatKey) — so a user learns the keyboard path just by looking, per the
// keyboard-first principle. The shortcuts are never hardcoded: they track ~/.weavie/keybindings.json.
interface StarterAction {
  id: string;
  label: string;
  hint: string;
  icon: (props: { size?: number | string }) => JSX.Element;
}

const ACTIONS: StarterAction[] = [
  {
    id: CommandIds.focusOmnibarFiles,
    label: "Go to File",
    hint: "Jump to any file by name",
    icon: FileSearch,
  },
  {
    id: CommandIds.toggleFileBrowser,
    label: "Browse Files",
    hint: "Open the workspace file tree",
    icon: FolderTree,
  },
  {
    id: CommandIds.newFile,
    label: "New File",
    hint: "Start an untitled scratch buffer",
    icon: FilePlus,
  },
  {
    id: CommandIds.focusOmnibarCommands,
    label: "Show All Commands",
    hint: "Open the command palette",
    icon: Command,
  },
];

// The action's effective shortcut (defaults merged with the user's keybindings), formatted for display, or
// "" when the command is unbound — in which case the row just shows the label.
function keysOf(id: string): string {
  const keys = findCommand(id)?.keys ?? [];
  return keys.length > 0 ? keys.map(formatKey).join(" / ") : "";
}

export function EditorEmptyState(): JSX.Element {
  return (
    <div class="editor-empty" data-kind="editor">
      <div class="editor-empty-inner">
        <header class="editor-empty-head">
          <span class="editor-empty-mark" aria-hidden="true">
            <WeavieIcon />
          </span>
          <div class="editor-empty-titles">
            <h1 class="editor-empty-wordmark">weavie</h1>
            <p class="editor-empty-tagline">No file open. Open one to start editing.</p>
          </div>
        </header>
        <ul class="editor-empty-actions">
          <For each={ACTIONS}>
            {(action) => {
              const keys = keysOf(action.id);
              const Icon = action.icon;
              return (
                <li>
                  <button
                    type="button"
                    class="editor-empty-action"
                    // Don't let the button steal focus on press: these actions hand focus to the omnibar
                    // (Go to File / commands), and a button holding focus trips the omnibar's focus-out
                    // close, snapping it shut and focus back here. preventDefault on mousedown blocks the
                    // focus without cancelling the click, so keyboard activation (Tab+Enter) still fires it.
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => dispatchCommand(action.id)}
                  >
                    <Icon size="1.15em" />
                    <span class="editor-empty-action-text">
                      <span class="editor-empty-action-label">{action.label}</span>
                      <span class="editor-empty-action-hint">{action.hint}</span>
                    </span>
                    {keys !== "" && <kbd class="editor-empty-keys">{keys}</kbd>}
                  </button>
                </li>
              );
            }}
          </For>
        </ul>
      </div>
    </div>
  );
}
