import { Pin, X } from "lucide-solid";
import { For, type JSX, Show, createMemo, createSignal, onCleanup } from "solid-js";
import { Portal } from "solid-js/web";
import { formatKey } from "../commands/keybindings";
import { dispatchCommand, findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { TabActions } from "./editor-controller";
import type { EditorSessionEntry } from "./session-types";

// The structural fields the strip renders. Deliberately excludes view state, so the strip doesn't re-render
// as the cursor/scroll updates the active tab's saved position.
interface TabView {
  path: string;
  preview: boolean;
  pinned: boolean;
}

function basename(path: string): string {
  const parts = path.split(/[\\/]/).filter((part) => part.length > 0);
  return parts.length > 0 ? (parts[parts.length - 1] as string) : path;
}

/**
 * The editor tab strip: one row per open file, mounted inside the editor pane (NOT a layout pane). Renders
 * from the tab store and drives the controller's tab actions. Mouse gestures manipulate tabs directly; the
 * right-click menu dispatches the editor-tab COMMANDS (so it's consistent with the palette / Claude and can
 * advertise each action's shortcut). Keyboard-first: every menu row that has a binding shows it.
 */
export function TabStrip(props: {
  tabs: () => EditorSessionEntry[];
  activePath: () => string | null;
  actions: TabActions;
}): JSX.Element {
  const sameTabs = (a: TabView[], b: TabView[]): boolean =>
    a.length === b.length &&
    a.every((tab, i) => {
      const other = b[i];
      return (
        other !== undefined &&
        tab.path === other.path &&
        tab.preview === other.preview &&
        tab.pinned === other.pinned
      );
    });
  const views = createMemo<TabView[]>(
    () =>
      props.tabs().map((tab) => ({
        path: tab.path,
        preview: tab.preview === true,
        pinned: tab.pinned === true,
      })),
    [],
    { equals: sameTabs },
  );
  const active = createMemo(() => props.activePath());

  // Right-click context menu, anchored at the cursor and targeting the right-clicked tab.
  const [menu, setMenu] = createSignal<{
    x: number;
    y: number;
    path: string;
    pinned: boolean;
  } | null>(null);
  const closeMenu = (): void => {
    setMenu(null);
  };

  const onPointerDown = (event: PointerEvent): void => {
    if (!(event.target as HTMLElement).closest(".tab-ctx-menu")) {
      closeMenu();
    }
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      closeMenu();
    }
  };
  window.addEventListener("pointerdown", onPointerDown);
  window.addEventListener("keydown", onKeyDown);
  window.addEventListener("blur", closeMenu);
  onCleanup(() => {
    window.removeEventListener("pointerdown", onPointerDown);
    window.removeEventListener("keydown", onKeyDown);
    window.removeEventListener("blur", closeMenu);
  });

  const openMenu = (event: MouseEvent, view: TabView): void => {
    event.preventDefault();
    setMenu({ x: event.clientX, y: event.clientY, path: view.path, pinned: view.pinned });
  };
  const run = (id: string, path: string): void => {
    closeMenu();
    dispatchCommand(id, { path });
  };
  // A menu row's current shortcut (defaults merged with the user's keybindings.json), or "" if unbound.
  const keysOf = (id: string): string => {
    const keys = findCommand(id)?.keys ?? [];
    return keys.length > 0 ? keys.map(formatKey).join(" / ") : "";
  };

  return (
    <>
      <Show when={views().length > 0}>
        <div class="editor-tabs">
          <For each={views()}>
            {(view) => (
              <div
                class="editor-tab"
                classList={{
                  active: active() === view.path,
                  preview: view.preview,
                  pinned: view.pinned,
                }}
                title={view.path}
              >
                <button
                  type="button"
                  class="editor-tab-main"
                  onClick={() => props.actions.activate(view.path)}
                  onDblClick={() => props.actions.promote(view.path)}
                  onMouseDown={(event) => {
                    // Middle-click closes (a familiar tab gesture); preventDefault avoids autoscroll.
                    if (event.button === 1) {
                      event.preventDefault();
                      props.actions.close(view.path);
                    }
                  }}
                  onContextMenu={(event) => openMenu(event, view)}
                >
                  <span class="editor-tab-label">{basename(view.path)}</span>
                </button>
                <button
                  type="button"
                  class="editor-tab-close"
                  title={view.pinned ? "Unpin" : "Close"}
                  onClick={() => {
                    if (view.pinned) {
                      props.actions.togglePin(view.path);
                    } else {
                      props.actions.close(view.path);
                    }
                  }}
                >
                  {view.pinned ? <Pin size={13} /> : <X size={13} />}
                </button>
              </div>
            )}
          </For>
        </div>
      </Show>
      <Show when={menu()}>
        {(m) => (
          <Portal>
            <div
              class="tab-ctx-menu"
              ref={(el) => {
                // Position at the cursor imperatively — the menu mounts fresh on each open, so the ref fires
                // with the current coords. (Avoids an object `style={{}}` binding, unsupported by this
                // solid-js/web build — it compiles to a missing `setStyleProperty` import.)
                el.style.left = `${m().x}px`;
                el.style.top = `${m().y}px`;
              }}
            >
              <button
                type="button"
                class="tab-ctx-item"
                onClick={() => run(CommandIds.closeTab, m().path)}
              >
                <span>Close</span>
                <span class="tab-ctx-keys">{keysOf(CommandIds.closeTab)}</span>
              </button>
              <button
                type="button"
                class="tab-ctx-item"
                onClick={() => run(CommandIds.closeOtherTabs, m().path)}
              >
                <span>Close Others</span>
              </button>
              <button
                type="button"
                class="tab-ctx-item"
                onClick={() => run(CommandIds.closeTabsToLeft, m().path)}
              >
                <span>Close to the Left</span>
              </button>
              <button
                type="button"
                class="tab-ctx-item"
                onClick={() => run(CommandIds.closeTabsToRight, m().path)}
              >
                <span>Close to the Right</span>
              </button>
              <button
                type="button"
                class="tab-ctx-item"
                onClick={() => run(CommandIds.closeAllTabs, m().path)}
              >
                <span>Close All</span>
              </button>
              <div class="tab-ctx-sep" />
              <button
                type="button"
                class="tab-ctx-item"
                onClick={() => run(CommandIds.togglePinTab, m().path)}
              >
                <span>{m().pinned ? "Unpin" : "Pin"}</span>
                <span class="tab-ctx-keys">{keysOf(CommandIds.togglePinTab)}</span>
              </button>
            </div>
          </Portal>
        )}
      </Show>
    </>
  );
}
