import { ChevronRight } from "lucide-solid";
import { createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { LOCAL_BACKEND_ID } from "../bridge";
import { evaluateWhen } from "../commands/context";
import { keyLabelInCatalog } from "../commands/key-hint";
import { findCommandInCatalog, runCommandFromCatalogWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { dismissOnOutsideInteraction } from "./popover-dismiss";

// The resolved shortcut for a command, formatted for display ("" when unbound) — so a menu item advertises
// its keybinding (keyboard-first), read live from the catalog rather than hardcoded.
function shortcutOf(commandId: string): string {
  return keyLabelInCatalog(LOCAL_BACKEND_ID, commandId);
}

function available(commandId: string): boolean {
  const command = findCommandInCatalog(LOCAL_BACKEND_ID, commandId);
  return command !== undefined && evaluateWhen(command.when);
}

function leaf(path: string): string {
  const parts = path.split(/[\\/]/).filter((p) => p.length > 0);
  return parts.length > 0 ? (parts[parts.length - 1] as string) : path;
}

// The app bar's File + View menus. Every row invokes its command id; one menu stays open at a time, and
// hovering the other label while open switches to it.
export function Menu(props: { recents: string[] }): JSX.Element {
  const [openMenu, setOpenMenu] = createSignal<"file" | "view" | null>(null);

  const close = (): void => {
    setOpenMenu(null);
  };
  const toggle = (menu: "file" | "view"): void => {
    setOpenMenu((m) => (m === menu ? null : menu));
  };
  const hover = (menu: "file" | "view"): void => {
    if (openMenu() !== null) {
      setOpenMenu(menu);
    }
  };

  // Dismiss on any outside pointer-down, a window blur, or Escape while a menu is open.
  dismissOnOutsideInteraction(".tb-menu", close);
  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "Escape") {
      close();
    }
  };
  window.addEventListener("keydown", onKeyDown);
  onCleanup(() => window.removeEventListener("keydown", onKeyDown));

  const commandAction = (command: string, args?: unknown): void => {
    close();
    void runCommandFromCatalogWithFeedback(LOCAL_BACKEND_ID, command, args);
  };

  return (
    <div class="tb-menu">
      <Show when={available(CommandIds.openFolder)}>
        <div class="tb-menu-item">
          <button
            type="button"
            class="tb-menu-label"
            classList={{ open: openMenu() === "file" }}
            onClick={() => toggle("file")}
            onMouseEnter={() => hover("file")}
          >
            File
          </button>
          <Show when={openMenu() === "file"}>
            <div class="tb-dropdown">
              <button
                type="button"
                class="tb-dropitem"
                onClick={() => commandAction(CommandIds.openFolder)}
              >
                <span>Open Folder…</span>
                <Show when={shortcutOf(CommandIds.openFolder)}>
                  {(keys) => <span class="tb-dropitem-keys">{keys()}</span>}
                </Show>
              </button>
              <div
                class="tb-dropitem has-submenu"
                classList={{ disabled: props.recents.length === 0 }}
                // Focusable (unless empty) so a keyboard user can reach it; the submenu reveals on :focus-within.
                tabindex={props.recents.length === 0 ? undefined : 0}
              >
                <span>Open Recent</span>
                <span class="tb-submenu-arrow">
                  <ChevronRight />
                </span>
                <Show when={props.recents.length > 0}>
                  <div class="tb-submenu">
                    <For each={props.recents}>
                      {(path) => (
                        <button
                          type="button"
                          class="tb-dropitem"
                          title={path}
                          onClick={() => commandAction(CommandIds.openRecentWorkspace, { path })}
                        >
                          <span class="tb-recent-leaf">{leaf(path)}</span>
                          <span class="tb-recent-path">{path}</span>
                        </button>
                      )}
                    </For>
                  </div>
                </Show>
              </div>
              <div class="tb-sep" />
              <button
                type="button"
                class="tb-dropitem"
                onClick={() => commandAction(CommandIds.closeWindow)}
              >
                <span>Close Window</span>
                <Show when={shortcutOf(CommandIds.closeWindow)}>
                  {(keys) => <span class="tb-dropitem-keys">{keys()}</span>}
                </Show>
              </button>
              <button
                type="button"
                class="tb-dropitem"
                onClick={() => commandAction(CommandIds.exit)}
              >
                <span>Exit</span>
                <Show when={shortcutOf(CommandIds.exit)}>
                  {(keys) => <span class="tb-dropitem-keys">{keys()}</span>}
                </Show>
              </button>
            </div>
          </Show>
        </div>
      </Show>

      <div class="tb-menu-item">
        <button
          type="button"
          class="tb-menu-label"
          classList={{ open: openMenu() === "view" }}
          onClick={() => toggle("view")}
          onMouseEnter={() => hover("view")}
        >
          View
        </button>
        <Show when={openMenu() === "view"}>
          <div class="tb-dropdown">
            <button
              type="button"
              class="tb-dropitem"
              onClick={() => commandAction(CommandIds.toggleFileBrowser)}
            >
              <span>Toggle Files</span>
              <Show when={shortcutOf(CommandIds.toggleFileBrowser)}>
                {(keys) => <span class="tb-dropitem-keys">{keys()}</span>}
              </Show>
            </button>
          </div>
        </Show>
      </div>
    </div>
  );
}
