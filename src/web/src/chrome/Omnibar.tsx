import { Search } from "lucide-solid";
import {
  For,
  type JSX,
  Show,
  createEffect,
  createMemo,
  createSignal,
  on,
  onCleanup,
} from "solid-js";
import { evaluateWhen } from "../commands/context";
import { formatKey } from "../commands/keybindings";
import { dispatchCommand, getCommands, onCommandsChanged } from "../commands/registry";
import type { CommandInfo } from "../commands/types";
import { fuzzyMatch } from "./fuzzy";
import { omnibarRequest } from "./omnibar-controller";

// Max rows rendered at once. With no query the rendered window is centered on the current file, so the
// list "centers around" the open file without mounting thousands of rows for a large workspace.
const VIEW_CAP = 300;

interface Row {
  abs: string;
  rel: string;
  leaf: string;
  dir: string;
}

function splitPath(abs: string, root: string): Row {
  let rel = abs;
  if (root.length > 0 && abs.toLowerCase().startsWith(root.toLowerCase())) {
    rel = abs.slice(root.length).replace(/^[\\/]+/, "");
  }
  const norm = rel.replace(/\\/g, "/");
  const slash = norm.lastIndexOf("/");
  return {
    abs,
    rel: norm,
    leaf: slash >= 0 ? norm.slice(slash + 1) : norm,
    dir: slash >= 0 ? norm.slice(0, slash) : "",
  };
}

// The center omnibar: a VS Code–style quick-open. Default mode is "Go to File" (fuzzy over the workspace
// file index, opens the file). A leading ">" flips to the command palette (fuzzy over the command catalog,
// Enter runs the command). Focusing it asks the host for the file index; the focus-omnibar commands open it
// programmatically (via omnibarRequest) in either mode. Replaces the old floating "Files" button on Windows.
export function Omnibar(props: {
  files: string[];
  root: string | null;
  currentFile: string | null;
  workspaceLabel: string;
  onOpenFile: (abs: string) => void;
  onRequestIndex: () => void;
}): JSX.Element {
  const [query, setQuery] = createSignal("");
  const [open, setOpen] = createSignal(false);
  const [selected, setSelected] = createSignal(0);
  let inputRef!: HTMLInputElement;
  let listRef: HTMLUListElement | undefined;

  // The command catalog, kept live as the host pushes keybinding/catalog changes.
  const [commandList, setCommandList] = createSignal<CommandInfo[]>(getCommands());
  onCleanup(onCommandsChanged(() => setCommandList(getCommands())));

  const rows = createMemo<Row[]>(() => {
    const root = props.root ?? "";
    return props.files.map((abs) => splitPath(abs, root));
  });

  const commandMode = (): boolean => query().startsWith(">");

  // The full filtered file list (uncapped): empty query → alpha order; otherwise fuzzy-ranked best-first.
  const filtered = createMemo<Row[]>(() => {
    if (commandMode()) {
      return [];
    }
    const q = query().trim();
    const all = rows();
    if (q.length === 0) {
      return [...all].sort((a, b) => a.rel.localeCompare(b.rel));
    }
    const scored: { row: Row; score: number }[] = [];
    for (const row of all) {
      const m = fuzzyMatch(q, row.rel);
      if (m !== null) {
        scored.push({ row, score: m.score });
      }
    }
    scored.sort((a, b) => b.score - a.score);
    return scored.map((s) => s.row);
  });

  // What actually renders: when unfiltered, a VIEW_CAP window centered on the current file; else top matches.
  const view = createMemo<Row[]>(() => {
    const all = filtered();
    if (all.length <= VIEW_CAP) {
      return all;
    }
    if (query().trim().length === 0 && props.currentFile !== null) {
      const idx = all.findIndex((r) => r.abs === props.currentFile);
      if (idx >= 0) {
        const start = Math.max(0, Math.min(idx - Math.floor(VIEW_CAP / 2), all.length - VIEW_CAP));
        return all.slice(start, start + VIEW_CAP);
      }
    }
    return all.slice(0, VIEW_CAP);
  });

  // The palette: visible commands whose `when` passes, fuzzy-ranked over the query after the ">".
  const commandView = createMemo<CommandInfo[]>(() => {
    if (!commandMode()) {
      return [];
    }
    const all = commandList().filter((c) => c.showInPalette && evaluateWhen(c.when));
    const q = query().slice(1).trim();
    if (q.length === 0) {
      return [...all].sort(
        (a, b) =>
          (a.category ?? "").localeCompare(b.category ?? "") || a.title.localeCompare(b.title),
      );
    }
    const scored: { cmd: CommandInfo; score: number }[] = [];
    for (const cmd of all) {
      const m = fuzzyMatch(q, [cmd.title, cmd.category ?? "", ...cmd.aliases].join(" "));
      if (m !== null) {
        scored.push({ cmd, score: m.score });
      }
    }
    scored.sort((a, b) => b.score - a.score);
    return scored.map((s) => s.cmd);
  });

  const activeLen = (): number => (commandMode() ? commandView().length : view().length);
  const hiddenCount = (): number => Math.max(0, filtered().length - view().length);

  const scrollToSelected = (block: ScrollLogicalPosition): void => {
    (listRef?.children[selected()] as HTMLElement | undefined)?.scrollIntoView({ block });
  };

  // On open with no query, preselect + center the current file (file mode only).
  createEffect(
    on(open, (isOpen) => {
      if (!isOpen) {
        return;
      }
      const v = view();
      const idx = props.currentFile !== null ? v.findIndex((r) => r.abs === props.currentFile) : -1;
      setSelected(idx >= 0 ? idx : 0);
      queueMicrotask(() => scrollToSelected("center"));
    }),
  );

  // Reset to the top whenever the query changes (deferred so it doesn't fight the open-centering above).
  createEffect(
    on(
      query,
      () => {
        setSelected(0);
        queueMicrotask(() => scrollToSelected("nearest"));
      },
      { defer: true },
    ),
  );

  // A focus-omnibar command opened us: switch to the requested mode, focus the input, refresh the index.
  createEffect(
    on(
      omnibarRequest,
      (request) => {
        if (request === null) {
          return;
        }
        setQuery(request.mode === "command" ? ">" : "");
        setOpen(true);
        props.onRequestIndex();
        queueMicrotask(() => inputRef.focus());
      },
      { defer: true },
    ),
  );

  const close = (): void => {
    setOpen(false);
    setQuery("");
    inputRef.blur();
  };

  const choose = (row: Row | undefined): void => {
    if (row === undefined) {
      return;
    }
    props.onOpenFile(row.abs);
    close();
  };

  const runCommand = (cmd: CommandInfo | undefined): void => {
    if (cmd === undefined) {
      return;
    }
    dispatchCommand(cmd.id);
    close();
  };

  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setSelected((i) => Math.min(i + 1, activeLen() - 1));
      scrollToSelected("nearest");
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setSelected((i) => Math.max(i - 1, 0));
      scrollToSelected("nearest");
    } else if (e.key === "Enter") {
      e.preventDefault();
      if (commandMode()) {
        runCommand(commandView()[selected()]);
      } else {
        choose(view()[selected()]);
      }
    } else if (e.key === "Escape") {
      e.preventDefault();
      setOpen(false);
      inputRef.blur();
    }
  };

  // Close when focus leaves the omnibar entirely; a short delay lets a row's click register first.
  const onFocusOut = (e: FocusEvent): void => {
    const next = e.relatedTarget as Node | null;
    if (next === null || !(e.currentTarget as HTMLElement).contains(next)) {
      window.setTimeout(() => setOpen(false), 120);
    }
  };

  return (
    <div class="tb-omnibar" onFocusOut={onFocusOut}>
      <div class="tb-omnibar-box" classList={{ open: open() }}>
        <span class="tb-omnibar-icon" aria-hidden="true">
          <Search />
        </span>
        <input
          ref={inputRef}
          class="tb-omnibar-input"
          type="text"
          spellcheck={false}
          placeholder={props.workspaceLabel}
          value={query()}
          onInput={(e) => setQuery(e.currentTarget.value)}
          onFocus={() => {
            setOpen(true);
            props.onRequestIndex();
          }}
          onClick={() => setOpen(true)}
          onKeyDown={onKeyDown}
        />
      </div>
      <Show when={open()}>
        <div class="tb-omnibar-pop">
          <Show
            when={!commandMode()}
            fallback={
              <Show
                when={commandView().length > 0}
                fallback={<div class="tb-omnibar-empty">No matching commands</div>}
              >
                <ul class="tb-omnibar-list" ref={listRef}>
                  <For each={commandView()}>
                    {(cmd, i) => (
                      <li>
                        <button
                          type="button"
                          class="tb-omnibar-row"
                          classList={{ selected: i() === selected() }}
                          onMouseDown={(e) => {
                            e.preventDefault();
                            setSelected(i());
                            runCommand(cmd);
                          }}
                        >
                          <span class="tb-row-leaf">{cmd.title}</span>
                          <Show when={cmd.category}>
                            <span class="tb-row-dir">{cmd.category}</span>
                          </Show>
                          <Show when={cmd.keys.length > 0}>
                            <span class="tb-row-keys">{cmd.keys.map(formatKey).join(" / ")}</span>
                          </Show>
                        </button>
                      </li>
                    )}
                  </For>
                </ul>
              </Show>
            }
          >
            <Show
              when={view().length > 0}
              fallback={<div class="tb-omnibar-empty">No matching files</div>}
            >
              <ul class="tb-omnibar-list" ref={listRef}>
                <For each={view()}>
                  {(row, i) => (
                    <li>
                      <button
                        type="button"
                        class="tb-omnibar-row"
                        classList={{
                          selected: i() === selected(),
                          current: row.abs === props.currentFile,
                        }}
                        onMouseDown={(e) => {
                          // mousedown (not click) fires before the input's focusout closes the popover.
                          e.preventDefault();
                          setSelected(i());
                          choose(row);
                        }}
                      >
                        <span class="tb-row-leaf">{row.leaf}</span>
                        <Show when={row.dir.length > 0}>
                          <span class="tb-row-dir">{row.dir}</span>
                        </Show>
                      </button>
                    </li>
                  )}
                </For>
              </ul>
            </Show>
            <Show when={hiddenCount() > 0}>
              <div class="tb-omnibar-more">+{hiddenCount()} more — type to filter</div>
            </Show>
          </Show>
        </div>
      </Show>
    </div>
  );
}
