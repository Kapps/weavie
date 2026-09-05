import { History } from "lucide-solid";
import { createMemo, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { formatKey } from "../commands/keybindings";
import { findCommand, registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { canonicalFsPath } from "../editor/fs-path";
import { createListNavigation } from "../list-navigation";
import { createFileFinder, type FileRow, rankFiles, splitPath } from "./file-search";
import { dismissOnOutsideInteraction } from "./popover-dismiss";
import { recentFiles, recentFilesState } from "./recent-files-store";

// How many rows the dropdown shows at once. The host remembers many more (top 50, frecency-ranked); the search
// box filters across all of them, so a long history stays reachable without a long list.
const MAX_ROWS = 12;

/**
 * The editor status bar's recent-files control: a "Recent" button that toggles a dropdown of frecency-ranked
 * recent files with a search box that filters them the same way the omnibar does. The same dropdown is opened by
 * the `openRecentFiles` command (keybinding / palette / Claude), so the button advertises its shortcut. `onOpen`
 * opens the chosen file; `root` is the active worktree root, used to render/rank paths repo-relative.
 */
export function RecentFilesButton(props: {
  onOpen: (path: string) => void;
  root: () => string;
}): JSX.Element {
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
    <div class="footer-recent">
      <button
        type="button"
        class="footer-recent-toggle"
        title={title()}
        aria-haspopup="menu"
        aria-expanded={open()}
        onMouseDown={(event) => event.preventDefault()}
        onClick={() => setOpen((v) => !v)}
      >
        <History size={13} />
        <span>Recent</span>
      </button>
      <Show when={open()}>
        <RecentFilesMenu root={props.root} onChoose={choose} onClose={() => setOpen(false)} />
      </Show>
    </div>
  );
}

// The dropdown: a search box over the recent-files list (filtered by the omnibar's rankFiles) plus the results.
// Portaled to <body>, anchored above the status bar, dismissed on outside-click / Escape / blur.
function RecentFilesMenu(props: {
  root: () => string;
  onChoose: (path: string) => void;
  onClose: () => void;
}): JSX.Element {
  let panelEl: HTMLDivElement | undefined;
  let inputEl: HTMLInputElement | undefined;
  const [query, setQuery] = createSignal("");

  // Empty query → the recent list in frecency order; a query → the omnibar's fuzzy ranking over the same set.
  const results = createMemo<FileRow[]>(() => {
    const rows = recentFiles().map((abs) => splitPath(abs, props.root()));
    const q = query().trim();
    if (q.length === 0) {
      return rows.slice(0, MAX_ROWS);
    }
    // No proximity bias here (currentDir null): a Recent menu stays most-recent-first among equal matches.
    return rankFiles(createFileFinder(rows), q, recentFiles(), null)
      .slice(0, MAX_ROWS)
      .map((s) => s.row);
  });
  const emptyLabel = (): string => {
    const state = recentFilesState();
    if (state.loading) {
      return "Loading files…";
    }
    if (query().trim().length > 0) {
      return "No matching files.";
    }
    return state.hasHistory ? "No recent files in this checkout." : "No recent files yet.";
  };

  // clampIndex keeps the highlighted row in range as the results change; onMove keeps it scrolled into view.
  const nav = createListNavigation({
    count: () => results().length,
    edges: "wrap",
    initialIndex: 0,
    acceptKeys: ["Enter"],
    onAccept: (index) => {
      const row = results()[index];
      if (row !== undefined) {
        props.onChoose(row.abs);
      }
    },
    onDismiss: () => props.onClose(),
    onMove: (index) =>
      queueMicrotask(() =>
        panelEl
          ?.querySelectorAll<HTMLElement>(".recent-row")
          [index]?.scrollIntoView({ block: "nearest" }),
      ),
    consumeEmptyArrows: true,
    clampIndex: true,
  });

  // Anchor to the toggle, right-aligned and opening UPWARD (the toggle is in the bottom status bar): pin the
  // panel's bottom just above the toggle so it grows up as the result list changes, and cap its height to the
  // space above so it scrolls rather than spilling off-screen.
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
    panelEl.style.bottom = `${window.innerHeight - anchor.top + 2}px`;
    panelEl.style.maxHeight = `${anchor.top - margin - 2}px`;
  };

  // Escape is handled on the input while it holds focus; this backstops a Tab that moved focus onto a row.
  const onWindowKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      props.onClose();
    }
  };

  onMount(() => {
    const toggle = document.querySelector(".footer-recent-toggle");
    queueMicrotask(() => {
      if (toggle !== null) {
        place(toggle);
      }
      inputEl?.focus();
    });
    // The toggle counts as inside: its own onClick closes the menu, so the two don't race.
    dismissOnOutsideInteraction(".recent-menu, .footer-recent-toggle", props.onClose);
    window.addEventListener("keydown", onWindowKeyDown);
    onCleanup(() => window.removeEventListener("keydown", onWindowKeyDown));
  });

  return (
    <Portal>
      <div class="recent-menu" ref={panelEl}>
        <input
          class="recent-search"
          ref={inputEl}
          type="text"
          placeholder="Search recent files…"
          value={query()}
          spellcheck={false}
          autocomplete="off"
          onInput={(event) => {
            setQuery(event.currentTarget.value);
            nav.setIndex(0);
          }}
          onKeyDown={nav.onKeyDown}
        />
        <div class="recent-list" aria-busy={recentFilesState().loading}>
          <Show
            when={results().length > 0}
            fallback={<div class="recent-empty">{emptyLabel()}</div>}
          >
            <For each={results()}>
              {(row, index) => (
                <button
                  type="button"
                  class="recent-row"
                  classList={{ selected: index() === nav.index() }}
                  title={row.abs}
                  onMouseMove={() => nav.setIndex(index())}
                  onClick={() => props.onChoose(row.abs)}
                >
                  <span class="recent-name">{row.leaf}</span>
                  <Show when={row.dir.length > 0}>
                    <span class="recent-dir">{row.dir}</span>
                  </Show>
                </button>
              )}
            </For>
          </Show>
        </div>
      </div>
    </Portal>
  );
}
