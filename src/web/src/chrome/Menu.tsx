import { createEffect, createSignal, For, type JSX, on, onCleanup, Show, untrack } from "solid-js";
import {
  type ContextOverrides,
  evaluateWhen,
  onContextChanged,
  paneFocusContext,
} from "../commands/context";
import { findCommand, onCommandsChanged } from "../commands/registry";
import { CommandIds, type CommandInfo } from "../commands/types";
import {
  APPLICATION_MENUS,
  type ApplicationMenuDefinition,
  type ApplicationMenuEntry,
} from "./application-menu";
import {
  ContextMenu,
  type ContextMenuEntry,
  type ContextMenuItem,
  type ContextMenuState,
} from "./ContextMenu";
import { recentWorkspaces } from "./recent-workspaces";

interface OpenApplicationMenu extends ContextMenuState {
  id: string;
}

function leaf(path: string): string {
  const parts = path.split(/[\\/]/).filter((part) => part.length > 0);
  return parts.length > 0 ? (parts[parts.length - 1] as string) : path;
}

function commandInfo(id: string): CommandInfo {
  const command = findCommand(id);
  if (command === undefined) {
    throw new Error(`Application menu references unknown command '${id}'.`);
  }
  return command;
}

function commandItem(
  id: string,
  context: ContextOverrides,
  args?: unknown,
  label?: string,
  title?: string,
): ContextMenuItem {
  const command = commandInfo(id);
  return {
    commandId: id,
    disabled: !evaluateWhen(command.when, context),
    ...(args === undefined ? {} : { args }),
    ...(label === undefined ? {} : { label }),
    ...(title === undefined ? {} : { title }),
  };
}

function normalize(entries: Array<ContextMenuEntry | null>): ContextMenuEntry[] {
  const normalized: ContextMenuEntry[] = [];
  for (const entry of entries) {
    if (entry === null) {
      continue;
    }
    if (entry.kind === "separator") {
      if (normalized.length === 0 || normalized[normalized.length - 1]?.kind === "separator") {
        continue;
      }
    }
    normalized.push(entry);
  }
  if (normalized[normalized.length - 1]?.kind === "separator") {
    normalized.pop();
  }
  return normalized;
}

function buildEntries(
  definitions: ApplicationMenuEntry[],
  recents: readonly string[],
  platform: string,
  context: ContextOverrides,
): ContextMenuEntry[] {
  return normalize(
    definitions.map((definition): ContextMenuEntry | null => {
      if (definition.kind === "separator") {
        return { kind: "separator" };
      }
      if (definition.kind === "recentWorkspaces") {
        const command = commandInfo(CommandIds.openRecentWorkspace);
        if (!evaluateWhen(command.when, context)) {
          return null;
        }
        return {
          kind: "submenu",
          label: command.title,
          disabled: recents.length === 0,
          entries: recents.map((path) =>
            commandItem(CommandIds.openRecentWorkspace, context, { path }, leaf(path), path),
          ),
        };
      }
      if (definition.kind === "submenu") {
        const entries = buildEntries(definition.entries, recents, platform, context);
        return {
          kind: "submenu",
          label: definition.label,
          entries,
          disabled: entries.every((entry) => entry.kind === "separator" || entry.disabled === true),
        };
      }
      if (definition.excludePlatforms?.includes(platform) === true) {
        return null;
      }
      const command = commandInfo(definition.commandId);
      if (command.when === "nativeShell" && !evaluateWhen(command.when, context)) {
        return null;
      }
      return commandItem(definition.commandId, context);
    }),
  );
}

/**
 * The shared Windows/macOS/Linux/web application menu. Its placement tree is curated once; every row joins
 * to the active command catalog for its label, context, effective shortcut, and dispatch owner.
 */
export function Menu(): JSX.Element {
  const platform = window.__WEAVIE_SHELL__?.platform ?? "web";
  const [openMenu, setOpenMenu] = createSignal<OpenApplicationMenu | null>(null);
  const [menuContext, setMenuContext] = createSignal<ContextOverrides>(
    paneFocusContext(document.activeElement),
  );
  const menuButtons = new Map<string, HTMLButtonElement>();
  let lastWorkspaceContext = paneFocusContext(document.activeElement);
  let hoverOpenedMenu: string | null = null;

  const close = (): void => {
    hoverOpenedMenu = null;
    setOpenMenu(null);
  };
  const open = (menu: ApplicationMenuDefinition, button: HTMLButtonElement): void => {
    hoverOpenedMenu = null;
    setMenuContext(lastWorkspaceContext);
    const rect = button.getBoundingClientRect();
    setOpenMenu({
      id: menu.id,
      x: rect.left,
      y: rect.bottom,
      entries: buildEntries(menu.entries, recentWorkspaces(), platform, lastWorkspaceContext),
    });
  };
  const toggle = (menu: ApplicationMenuDefinition, button: HTMLButtonElement): void => {
    if (hoverOpenedMenu === menu.id) {
      hoverOpenedMenu = null;
      return;
    }
    if (openMenu()?.id === menu.id) {
      close();
    } else {
      open(menu, button);
    }
  };
  const switchMenu = (step: number): void => {
    const current = APPLICATION_MENUS.findIndex((menu) => menu.id === openMenu()?.id);
    if (current < 0) {
      return;
    }
    const menu =
      APPLICATION_MENUS[(current + step + APPLICATION_MENUS.length) % APPLICATION_MENUS.length];
    const button = menu === undefined ? undefined : menuButtons.get(menu.id);
    if (menu !== undefined && button !== undefined) {
      open(menu, button);
    }
  };

  const onFocusIn = (event: FocusEvent): void => {
    const target = event.target;
    if (target instanceof Element && target.closest(".tb-menu, .context-menu") === null) {
      lastWorkspaceContext = paneFocusContext(target);
    }
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    if (openMenu() === null) {
      return;
    }
    if (event.key === "Escape") {
      const button = menuButtons.get(openMenu()?.id ?? "");
      close();
      button?.focus();
    } else if (event.key === "ArrowLeft") {
      event.preventDefault();
      switchMenu(-1);
    } else if (event.key === "ArrowRight") {
      event.preventDefault();
      switchMenu(1);
    }
  };
  document.addEventListener("focusin", onFocusIn);
  window.addEventListener("keydown", onKeyDown);
  const rebuildOpenMenu = (): void => {
    const current = untrack(openMenu);
    const menu = APPLICATION_MENUS.find((candidate) => candidate.id === current?.id);
    if (current !== null && menu !== undefined) {
      setOpenMenu({
        ...current,
        entries: buildEntries(menu.entries, recentWorkspaces(), platform, menuContext()),
      });
    }
  };
  const stopCommandsChanged = onCommandsChanged(rebuildOpenMenu);
  const stopContextChanged = onContextChanged(rebuildOpenMenu);
  createEffect(on(recentWorkspaces, rebuildOpenMenu, { defer: true }));
  onCleanup(() => {
    document.removeEventListener("focusin", onFocusIn);
    window.removeEventListener("keydown", onKeyDown);
    stopCommandsChanged();
    stopContextChanged();
  });

  return (
    <div class="tb-menu" role="menubar" aria-label="Application menu">
      <For each={APPLICATION_MENUS}>
        {(menu, index) => (
          <button
            type="button"
            class="tb-menu-label"
            classList={{ open: openMenu()?.id === menu.id }}
            role="menuitem"
            aria-haspopup="menu"
            aria-expanded={openMenu()?.id === menu.id}
            ref={(button) => menuButtons.set(menu.id, button)}
            onClick={(event) => toggle(menu, event.currentTarget)}
            onMouseEnter={(event) => {
              if (openMenu() !== null && openMenu()?.id !== menu.id) {
                open(menu, event.currentTarget);
                hoverOpenedMenu = menu.id;
              }
            }}
            onMouseLeave={() => {
              if (hoverOpenedMenu === menu.id) {
                hoverOpenedMenu = null;
              }
            }}
            onKeyDown={(event) => {
              if (event.key === "ArrowDown") {
                event.preventDefault();
                open(menu, event.currentTarget);
              } else if (
                openMenu() === null &&
                (event.key === "ArrowLeft" || event.key === "ArrowRight")
              ) {
                event.preventDefault();
                const step = event.key === "ArrowLeft" ? -1 : 1;
                const next =
                  APPLICATION_MENUS[
                    (index() + step + APPLICATION_MENUS.length) % APPLICATION_MENUS.length
                  ];
                if (next !== undefined) {
                  menuButtons.get(next.id)?.focus();
                }
              }
            }}
          >
            {menu.label}
          </button>
        )}
      </For>
      <Show when={openMenu()} keyed>
        {(menu) => (
          <ContextMenu menu={menu} dismissInside=".context-menu, .tb-menu" onClose={close} />
        )}
      </Show>
    </div>
  );
}
