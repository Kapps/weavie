import { History } from "lucide-solid";
import { For, type JSX, Show, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { formatKey } from "../commands/keybindings";
import { findCommand, registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { basename, canonicalFsPath } from "../editor/fs-path";
import { recentFiles } from "./recent-files-store";

// The most recent files to list; the frecency ranking already floats the useful ones to the top.
const MAX_ROWS = 12;

// An absolute path's file name plus its immediate parent folder (the recognizable "name — folder" pair). The
// name comes from `basename`; the parent is the segment before it, tolerating either separator.
function nameAndParent(path: string): { name: string; parent: string } {
  const parts = path.split(/[\\/]/).filter((p) => p.length > 0);
  return { name: basename(path), parent: parts[parts.length - 2] ?? "" };
}

/**
 * The editor tab bar's recent-files control: an icon button that toggles a dropdown of frecency-ranked recent
 * files. The same dropdown is opened by the `openRecentFiles` command (its keybinding / the palette / Claude),
 * so the button advertises that shortcut in its tooltip. Selecting a row opens the file via `onOpen`.
 */
export function RecentFilesButton(props: { onOpen: (path: string) => void }): JSX.Element {
  const [open, setOpen] = createSignal(false);

  // The verb plus the live shortcut from the command catalog (never hardcoded — it's user-overridable).
  const title = (): string => {
    const keys = findCommand(CommandIds.openRecentFiles)?.keys ?? [];
    const suffix = keys.length > 0 ? ` (${keys.map(formatKey).join(" / ")})` : "";
    return `Recent files${suffix}`;
  };

  onMount(() => {
    // The command toggles the same dropdown, so the keybinding / palette / Claude reach it too. Returns true to
    // consume the key (the dropdown owns it now).
    onCleanup(
      registerCommand(CommandIds.openRecentFiles, () => {
        setOpen((v) => !v);
        return true;
      }),
    );
  });

  const choose = (path: string): void => {
    setOpen(false);
    props.onOpen(canonicalFsPath(path));
  };

  return (
    <div class="editor-recent">
      <button
        type="button"
        class="editor-recent-toggle"
        title={title()}
        aria-haspopup="menu"
        aria-expanded={open()}
        onMouseDown={(event) => event.preventDefault()}
        onClick={() => setOpen((v) => !v)}
      >
        <History size={14} />
      </button>
      <Show when={open()}>
        <RecentFilesMenu onChoose={choose} onClose={() => setOpen(false)} />
      </Show>
    </div>
  );
}

// The dropdown panel: portaled to <body> so it escapes the tab bar's clipping, anchored under the toggle, with
// arrow-key roaming and outside-click / Escape / blur dismissal (mirroring ContextMenu's interaction model).
function RecentFilesMenu(props: {
  onChoose: (path: string) => void;
  onClose: () => void;
}): JSX.Element {
  let panelEl: HTMLDivElement | undefined;
  const files = (): readonly string[] => recentFiles().slice(0, MAX_ROWS);

  const rows = (): HTMLButtonElement[] =>
    panelEl ? [...panelEl.querySelectorAll<HTMLButtonElement>(".recent-row")] : [];

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") {
      return;
    }
    event.preventDefault();
    const list = rows();
    const current = list.indexOf(document.activeElement as HTMLButtonElement);
    const step = event.key === "ArrowDown" ? 1 : -1;
    const start = current < 0 ? (step === 1 ? -1 : 0) : current;
    list[(start + step + list.length) % list.length]?.focus();
  };

  // Anchor under the toggle, right-aligned to it, clamped into the viewport (the toggle sits at the strip's
  // right edge, so the panel grows leftward).
  const place = (toggle: Element): void => {
    if (panelEl === undefined) {
      return;
    }
    const anchor = toggle.getBoundingClientRect();
    const margin = 4;
    const width = panelEl.offsetWidth;
    const left = Math.max(
      margin,
      Math.min(anchor.right - width, window.innerWidth - width - margin),
    );
    panelEl.style.left = `${left}px`;
    panelEl.style.top = `${anchor.bottom + 2}px`;
  };

  const onPointerDown = (event: PointerEvent): void => {
    const target = event.target as HTMLElement;
    // A click on the toggle is handled by its own onClick (which closes); ignore it here so the two don't race.
    if (!target.closest(".recent-menu") && !target.closest(".editor-recent-toggle")) {
      props.onClose();
    }
  };
  const onEscape = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      props.onClose();
    }
  };

  onMount(() => {
    const toggle = document.querySelector(".editor-recent-toggle");
    queueMicrotask(() => {
      if (toggle !== null) {
        place(toggle);
      }
      rows()[0]?.focus();
    });
    window.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("keydown", onEscape);
    window.addEventListener("blur", props.onClose);
    onCleanup(() => {
      window.removeEventListener("pointerdown", onPointerDown);
      window.removeEventListener("keydown", onEscape);
      window.removeEventListener("blur", props.onClose);
    });
  });

  return (
    <Portal>
      <div class="recent-menu" ref={panelEl} onKeyDown={onKeyDown}>
        <div class="recent-menu-header">Recent files</div>
        <Show
          when={files().length > 0}
          fallback={<div class="recent-empty">No recent files yet.</div>}
        >
          <For each={files()}>
            {(path) => {
              const { name, parent } = nameAndParent(path);
              return (
                <button
                  type="button"
                  class="recent-row"
                  title={path}
                  onClick={() => props.onChoose(path)}
                >
                  <span class="recent-name">{name}</span>
                  <Show when={parent.length > 0}>
                    <span class="recent-dir">{parent}</span>
                  </Show>
                </button>
              );
            }}
          </For>
        </Show>
      </div>
    </Portal>
  );
}
