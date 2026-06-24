import { For, type JSX, Show, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { formatKey } from "../commands/keybindings";
import { dispatchCommand, findCommand } from "../commands/registry";
import { notify } from "../notify/notify";

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
  let menuEl: HTMLDivElement | undefined;
  const items = (): HTMLButtonElement[] =>
    menuEl ? [...menuEl.querySelectorAll<HTMLButtonElement>(".context-menu-item")] : [];
  const onPointerDown = (event: PointerEvent): void => {
    if (!(event.target as HTMLElement).closest(".context-menu")) {
      props.onClose();
    }
  };
  // Keyboard-operable like a real menu: Escape closes, Up/Down/Home/End roam the rows (Enter/Space fire the
  // focused row's command natively — they're buttons). Without this the menu's advertised shortcuts are
  // mouse-only.
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      props.onClose();
      return;
    }
    const list = items();
    if (list.length === 0) {
      return;
    }
    const current = list.indexOf(document.activeElement as HTMLButtonElement);
    let next: number | undefined;
    if (event.key === "ArrowDown") {
      next = current < 0 ? 0 : (current + 1) % list.length;
    } else if (event.key === "ArrowUp") {
      next = current < 0 ? list.length - 1 : (current - 1 + list.length) % list.length;
    } else if (event.key === "Home") {
      next = 0;
    } else if (event.key === "End") {
      next = list.length - 1;
    }
    if (next !== undefined) {
      event.preventDefault();
      list[next]?.focus();
    }
  };
  // Listeners are added on mount (after the opening right-click is handled) and torn down on close.
  onMount(() => {
    window.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("blur", props.onClose);
    // Land focus on the first row so arrow keys + Enter work immediately (queued so the For has rendered).
    queueMicrotask(() => items()[0]?.focus());
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
  // Dispatch the row's command and surface a failure as a toast (success is silent — the action's own effect,
  // e.g. a chip changing, is the feedback; a command that wants a success toast raises it itself).
  const run = async (item: ContextMenuItem): Promise<void> => {
    props.onClose();
    const result = await dispatchCommand(item.commandId, item.args);
    if (!result.ok && result.error !== undefined) {
      notify("error", result.error);
    }
  };

  return (
    <Portal>
      <div
        class="context-menu"
        ref={(el) => {
          menuEl = el;
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
