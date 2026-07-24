import { ChevronRight } from "lucide-solid";
import { createMemo, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { formatKey } from "../commands/keybindings";
import { findCommand, runCommandWithFeedback } from "../commands/registry";

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

// A nested fly-out: its own label plus the child entries it reveals, opened on hover or ArrowRight.
export interface ContextMenuSubmenu {
  kind: "submenu";
  label: string;
  entries: ContextMenuEntry[];
}

export type ContextMenuEntry = ContextMenuItem | ContextMenuSeparator | ContextMenuSubmenu;

// An open context menu: where to anchor it, an optional header (e.g. the target's name), and its entries.
export interface ContextMenuState {
  x: number;
  y: number;
  header?: string;
  entries: ContextMenuEntry[];
}

const labelOf = (item: ContextMenuItem): string =>
  item.label ?? findCommand(item.commandId)?.title ?? item.commandId;
const keysOf = (item: ContextMenuItem): string => {
  const keys = findCommand(item.commandId)?.keys ?? [];
  return keys.length > 0 ? keys.map(formatKey).join(" / ") : "";
};

/**
 * One panel of the menu — the root list or a submenu fly-out. Renders its rows, owns its own keyboard roaming
 * (Up/Down/Home/End between rows, ArrowRight to open the focused submenu, ArrowLeft to close back to the
 * parent), and portals each open submenu to <body> so it escapes the parent's clipping and own row queries.
 */
function MenuPanel(props: {
  entries: ContextMenuEntry[];
  x: number;
  y: number;
  header?: string | undefined;
  closeAll: () => void;
  // The opening row's rect, so a panel near the right edge flips to the parent's left instead of spilling.
  anchorRect?: DOMRect;
  // Focus the first row on mount (keyboard-opened submenu); a mouse-opened one leaves focus on the cursor.
  autoFocus?: boolean;
  // Close this panel and return focus to the row that opened it (the parent's ArrowLeft / Escape path).
  onCloseSelf?: () => void;
}): JSX.Element {
  let panelEl: HTMLDivElement | undefined;
  const [openEntry, setOpenEntry] = createSignal<ContextMenuSubmenu | null>(null);
  const [opener, setOpener] = createSignal<HTMLButtonElement | null>(null);
  const [anchorRect, setAnchorRect] = createSignal<DOMRect | null>(null);
  const [autoFocusChild, setAutoFocusChild] = createSignal(false);

  const rows = (): HTMLButtonElement[] =>
    panelEl ? [...panelEl.querySelectorAll<HTMLButtonElement>(".context-menu-item")] : [];

  // The open fly-out as one value, so the render never reads a half-set (entry without rect, or vice versa)
  // mid-update — both signals are folded here and the panel renders only when the whole thing is present.
  const openFlyout = createMemo(() => {
    const entry = openEntry();
    const rect = anchorRect();
    if (rect === null || entry === null || !props.entries.includes(entry)) {
      return null;
    }
    return { entries: entry.entries, rect };
  });

  const openSubmenu = (
    entry: ContextMenuSubmenu,
    row: HTMLButtonElement,
    viaKeyboard: boolean,
  ): void => {
    setOpener(row);
    // The panel has already clamped, so the row's rect is final — capture it to anchor the fly-out.
    setAnchorRect(row.getBoundingClientRect());
    setAutoFocusChild(viaKeyboard);
    setOpenEntry(entry);
  };
  const collapseSubmenu = (): void => {
    setOpenEntry(null);
    setAnchorRect(null);
  };
  const closeSubmenu = (): void => {
    const row = opener();
    collapseSubmenu();
    row?.focus();
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    const list = rows();
    const current = list.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const step = event.key === "ArrowDown" ? 1 : -1;
      const start = current < 0 ? (step === 1 ? -1 : 0) : current;
      list[(start + step + list.length) % list.length]?.focus();
    } else if (event.key === "Home") {
      event.preventDefault();
      list[0]?.focus();
    } else if (event.key === "End") {
      event.preventDefault();
      list[list.length - 1]?.focus();
    } else if (event.key === "ArrowRight") {
      const row = document.activeElement as HTMLButtonElement;
      const index = Number(row?.dataset.entryIndex);
      const entry = props.entries[index];
      if (Number.isInteger(index) && entry?.kind === "submenu") {
        event.preventDefault();
        openSubmenu(entry, row, true);
      }
    } else if (event.key === "ArrowLeft" && props.onCloseSelf !== undefined) {
      event.preventDefault();
      event.stopPropagation();
      props.onCloseSelf();
    }
  };

  // Position at (x, y), then shift to stay on-screen: a submenu near the right edge flips to the left of its
  // opener; the root simply slides in from the edge. Clamped to a small margin so an oversized menu still pins
  // top-left rather than scrolling its first rows off-screen.
  const clampToViewport = (): void => {
    if (panelEl === undefined) {
      return;
    }
    const margin = 4;
    const width = panelEl.offsetWidth;
    let x = props.x;
    if (x + width + margin > window.innerWidth) {
      x = props.anchorRect ? props.anchorRect.left - width : window.innerWidth - width - margin;
    }
    x = Math.max(margin, x);
    const y = Math.max(
      margin,
      Math.min(props.y, window.innerHeight - panelEl.offsetHeight - margin),
    );
    panelEl.style.left = `${x}px`;
    panelEl.style.top = `${y}px`;
  };

  onMount(() => {
    queueMicrotask(() => {
      clampToViewport();
      if (props.autoFocus) {
        rows()[0]?.focus();
      }
    });
  });

  const run = async (item: ContextMenuItem): Promise<void> => {
    props.closeAll();
    await runCommandWithFeedback(item.commandId, item.args);
  };

  return (
    <div
      class="context-menu"
      role="menu"
      ref={(el) => {
        panelEl = el;
        el.style.left = `${props.x}px`;
        el.style.top = `${props.y}px`;
      }}
      onKeyDown={onKeyDown}
    >
      <Show when={props.header}>
        <div class="context-menu-header">{props.header}</div>
      </Show>
      <For each={props.entries}>
        {(entry, index) =>
          entry.kind === "separator" ? (
            <div class="context-menu-sep" />
          ) : entry.kind === "submenu" ? (
            <button
              type="button"
              class="context-menu-item context-menu-submenu"
              data-entry-index={index()}
              aria-haspopup="true"
              aria-expanded={openEntry() === entry}
              onMouseEnter={(e) => openSubmenu(entry, e.currentTarget, false)}
              onClick={(e) => openSubmenu(entry, e.currentTarget, true)}
            >
              <span>{entry.label}</span>
              <ChevronRight size={14} class="context-menu-chevron" />
            </button>
          ) : (
            <button
              type="button"
              class={`context-menu-item${entry.danger ? " danger" : ""}`}
              data-entry-index={index()}
              onMouseEnter={() => collapseSubmenu()}
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
      <Show when={openFlyout()}>
        {(flyout) => (
          <Portal>
            <MenuPanel
              entries={flyout().entries}
              x={flyout().rect.right - 3}
              y={flyout().rect.top - 4}
              anchorRect={flyout().rect}
              autoFocus={autoFocusChild()}
              closeAll={props.closeAll}
              onCloseSelf={closeSubmenu}
            />
          </Portal>
        )}
      </Show>
    </div>
  );
}

/**
 * The one command-driven right-click menu for the whole app: every row dispatches a command (for palette /
 * Claude parity and shortcut advertising) and a submenu fans out into nested rows. Portaled to <body>,
 * positioned at the cursor, dismissed on outside-click / Escape / blur. Callers build the entries and own the
 * open signal. See docs/specs/commands.md.
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

  return (
    <Portal>
      <MenuPanel
        entries={props.menu.entries}
        x={props.menu.x}
        y={props.menu.y}
        header={props.menu.header}
        autoFocus
        closeAll={props.onClose}
      />
    </Portal>
  );
}
