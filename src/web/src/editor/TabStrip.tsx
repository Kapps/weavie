import { Pin, X } from "lucide-solid";
import { For, type JSX, Show, createMemo, createSignal } from "solid-js";
import { ContextMenu, type ContextMenuEntry, type ContextMenuState } from "../chrome/ContextMenu";
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
 * right-click menu uses the shared command-driven ContextMenu, dispatching the editor-tab COMMANDS (so it's
 * consistent with the palette / Claude and advertises each action's shortcut).
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

  // Right-click context menu, built for the right-clicked tab and rendered by the shared ContextMenu. Each row
  // targets that tab via the command's `path` arg; keyboard / palette omit it to act on the active tab.
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  const menuEntries = (view: TabView): ContextMenuEntry[] => {
    const args = { path: view.path };
    return [
      { commandId: CommandIds.closeTab, args, label: "Close" },
      { commandId: CommandIds.closeOtherTabs, args, label: "Close Others" },
      { commandId: CommandIds.closeTabsToLeft, args, label: "Close to the Left" },
      { commandId: CommandIds.closeTabsToRight, args, label: "Close to the Right" },
      { commandId: CommandIds.closeAllTabs, args, label: "Close All" },
      { kind: "separator" },
      { commandId: CommandIds.togglePinTab, args, label: view.pinned ? "Unpin" : "Pin" },
    ];
  };
  const openMenu = (event: MouseEvent, view: TabView): void => {
    event.preventDefault();
    setMenu({ x: event.clientX, y: event.clientY, entries: menuEntries(view) });
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
      <Show when={menu()}>{(m) => <ContextMenu menu={m()} onClose={() => setMenu(null)} />}</Show>
    </>
  );
}
