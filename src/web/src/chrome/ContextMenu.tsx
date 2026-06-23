import { For, type JSX, Show, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { formatKey } from "../commands/keybindings";
import { dispatchCommand, findCommand } from "../commands/registry";

// One row: a command to dispatch, an optional label override (defaults to the command's catalog title), and
// a danger flag for destructive actions.
export interface ContextMenuItem {
  kind?: "item";
  commandId: string;
  args?: unknown;
  label?: string;
  danger?: boolean;
}

export interface ContextMenuSeparator {
  kind: "separator";
}

export type ContextMenuEntry = ContextMenuItem | ContextMenuSeparator;

// An open context menu: where to anchor it, an optional header (e.g. the target's name), and its entries.
export interface ContextMenuState {
  x: number;
  y: number;
  header?: string;
  entries: ContextMenuEntry[];
}

/**
 * The one command-driven right-click menu for the whole app: every row dispatches a command (for palette /
 * Claude parity and shortcut advertising). Portaled to <body>, positioned at the cursor, dismissed on
 * outside-click / Escape / blur. Callers build the entries and own the open signal. See docs/specs/commands.md.
 */
export function ContextMenu(props: { menu: ContextMenuState; onClose: () => void }): JSX.Element {
  const onPointerDown = (event: PointerEvent): void => {
    if (!(event.target as HTMLElement).closest(".context-menu")) {
      props.onClose();
    }
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      props.onClose();
    }
  };
  // Listeners are added on mount (after the opening right-click is handled) and torn down on close.
  onMount(() => {
    window.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("blur", props.onClose);
  });
  onCleanup(() => {
    window.removeEventListener("pointerdown", onPointerDown);
    window.removeEventListener("keydown", onKeyDown);
    window.removeEventListener("blur", props.onClose);
  });

  const labelOf = (item: ContextMenuItem): string =>
    item.label ?? findCommand(item.commandId)?.title ?? item.commandId;
  const keysOf = (item: ContextMenuItem): string => {
    const keys = findCommand(item.commandId)?.keys ?? [];
    return keys.length > 0 ? keys.map(formatKey).join(" / ") : "";
  };
  const run = (item: ContextMenuItem): void => {
    props.onClose();
    dispatchCommand(item.commandId, item.args);
  };

  return (
    <Portal>
      <div
        class="context-menu"
        ref={(el) => {
          // Position at the cursor imperatively — the menu mounts fresh on each open.
          el.style.left = `${props.menu.x}px`;
          el.style.top = `${props.menu.y}px`;
        }}
      >
        <Show when={props.menu.header}>
          <div class="context-menu-header">{props.menu.header}</div>
        </Show>
        <For each={props.menu.entries}>
          {(entry) =>
            entry.kind === "separator" ? (
              <div class="context-menu-sep" />
            ) : (
              <button
                type="button"
                class={`context-menu-item${entry.danger ? " danger" : ""}`}
                onClick={() => run(entry)}
              >
                <span>{labelOf(entry)}</span>
                <Show when={keysOf(entry)}>
                  {(keys) => <span class="context-menu-keys">{keys()}</span>}
                </Show>
              </button>
            )
          }
        </For>
      </div>
    </Portal>
  );
}
