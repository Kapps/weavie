import {
  Command,
  FilePlus,
  FileSearch,
  FolderTree,
  GitBranch,
  GitPullRequestArrow,
} from "lucide-solid";
import { For, type JSX, Show } from "solid-js";
import { WeavieIcon } from "../chrome/WeavieIcon";
import { formatKey } from "../commands/keybindings";
import { findCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";

// Editor pane shown when no file is open. Each row is a button that dispatches a command and advertises its
// effective shortcut read from the command catalog (never hardcoded), so the keyboard path is discoverable.
interface StarterAction {
  id: string;
  label: string;
  hint: string;
  icon: (props: { size?: number | string }) => JSX.Element;
}

const ACTIONS: StarterAction[] = [
  {
    id: CommandIds.diffAgainstBranchBase,
    label: "Review Branch",
    hint: "See this branch’s commits and changes",
    icon: GitBranch,
  },
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

// The action's effective shortcut formatted for display, or "" when the command is unbound.
function keysOf(id: string): string {
  const keys = findCommand(id)?.keys ?? [];
  return keys.length > 0 ? keys.map(formatKey).join(" / ") : "";
}

// `reviewCount` is the number of files pending post-turn review. When no file is open the inline parked
// navigator can't render (it lives in the editor that isn't mounted), so this is the only place the user can
// see that Claude landed changes — exactly when they most need to know (#125).
export function EditorEmptyState(props: { reviewCount: number }): JSX.Element {
  return (
    <div class="editor-empty" data-kind="editor">
      <div class="editor-empty-inner">
        <header class="editor-empty-head">
          <span class="editor-empty-mark" aria-hidden="true">
            <WeavieIcon size={28} />
          </span>
          <div class="editor-empty-titles">
            <h1 class="editor-empty-wordmark">weavie</h1>
            <p class="editor-empty-tagline">No file open. Open one to start editing.</p>
          </div>
        </header>
        <Show when={props.reviewCount > 0}>
          <button
            type="button"
            class="editor-empty-review ui-row ui-row-stack"
            onMouseDown={(event) => event.preventDefault()}
            onClick={() => void runCommandWithFeedback(CommandIds.reviewOpen)}
          >
            <span class="ui-icon">
              <GitPullRequestArrow />
            </span>
            <span class="editor-empty-action-text ui-row-text">
              <span class="editor-empty-action-label">
                Review changes — {props.reviewCount} file{props.reviewCount === 1 ? "" : "s"}
              </span>
              <span class="editor-empty-action-hint ui-row-desc">
                See every changed file and diff in one view.
              </span>
            </span>
            {keysOf(CommandIds.reviewOpen) !== "" && (
              <kbd class="editor-empty-keys ui-kbd ui-row-end">{keysOf(CommandIds.reviewOpen)}</kbd>
            )}
          </button>
        </Show>
        <ul class="editor-empty-actions">
          <For each={ACTIONS}>
            {(action) => {
              const keys = keysOf(action.id);
              const Icon = action.icon;
              return (
                <li>
                  <button
                    type="button"
                    class="editor-empty-action ui-row ui-row-stack"
                    title={keys === "" ? action.label : `${action.label} (${keys})`}
                    // Don't steal focus on press: these actions hand focus to the omnibar, and a button
                    // holding focus would trip its focus-out close. preventDefault keeps the click firing.
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => void runCommandWithFeedback(action.id)}
                  >
                    <span class="ui-icon">
                      <Icon />
                    </span>
                    <span class="editor-empty-action-text ui-row-text">
                      <span class="editor-empty-action-label">{action.label}</span>
                      <span class="editor-empty-action-hint ui-row-desc">{action.hint}</span>
                    </span>
                    {keys !== "" && <kbd class="editor-empty-keys ui-kbd ui-row-end">{keys}</kbd>}
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
