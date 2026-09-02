import { createEffect, createSignal, For, type JSX, on, onCleanup, Show, untrack } from "solid-js";
import { type ContextOverrides, onContextChanged, paneFocusContext } from "../commands/context";
import { onCommandsChanged } from "../commands/registry";
import { APPLICATION_MENUS, type ApplicationMenuDefinition } from "./application-menu";
import { buildApplicationMenuEntries } from "./application-menu-model";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { recentWorkspaces } from "./recent-workspaces";

interface OpenApplicationMenu extends ContextMenuState {
  id: string;
}

/**
 * The Windows/Linux/web application menu. macOS consumes the same resolved model through its AppKit bridge.
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
      entries: buildApplicationMenuEntries(
        menu.entries,
        recentWorkspaces(),
        platform,
        lastWorkspaceContext,
      ),
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
        entries: buildApplicationMenuEntries(
          menu.entries,
          recentWorkspaces(),
          platform,
          menuContext(),
        ),
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
