import { byLengthAsc, Fzf } from "fzf";
import {
  Box,
  Braces,
  ChevronDown,
  ChevronRight,
  File as FileIcon,
  Folder,
  FolderOpen,
  Hash,
  Search,
  Type,
  Variable,
} from "lucide-solid";
import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  on,
  onCleanup,
  Show,
} from "solid-js";
import { evaluateWhen, paneFocusContext } from "../commands/context";
import { formatKey } from "../commands/keybindings";
import { getCommands, onCommandsChanged, runCommandWithFeedback } from "../commands/registry";
import { CommandIds, type CommandInfo } from "../commands/types";
import { canonicalFsPath, samePath } from "../editor/fs-path";
import type { FlatSymbol, SymbolActions } from "../symbols/symbol-match";
import { createSymbolSearch } from "../symbols/symbol-search";
import {
  createFileFinder,
  type FileRow,
  rankFiles,
  type ScoredFile,
  splitPath,
} from "./file-search";
import { highlightSlice } from "./highlight";
import { type OmnibarMode, omnibarRequest } from "./omnibar-controller";
import { recentFiles } from "./recent-files-store";

// Max rows rendered at once — a safety cap so a giant workspace never mounts thousands of rows.
const VIEW_CAP = 300;

// The leading character that selects each mode (empty = the file tree/list).
const MODE_PREFIX: Record<OmnibarMode, string> = {
  file: "",
  command: ">",
  docSymbol: "@",
  wsSymbol: "#",
};

// The omnibar's own focus commands → the mode they open. Run from the palette, they switch mode in place instead
// of round-tripping through the dispatcher, whose close()+refocus races the query reset and drops the mode.
const FOCUS_COMMAND_MODE: Record<string, OmnibarMode> = {
  [CommandIds.focusOmnibarFiles]: "file",
  [CommandIds.focusOmnibarCommands]: "command",
  [CommandIds.goToSymbol]: "docSymbol",
  [CommandIds.goToWorkspaceSymbol]: "wsSymbol",
};

// A node in the client-side file tree. `key` (the dir's relative path) is the expansion-state key.
interface TreeNode {
  name: string;
  key: string;
  isDir: boolean;
  abs?: string;
  children?: TreeNode[];
}

interface TreeRow {
  node: TreeNode;
  depth: number;
}

// Build a sorted tree (dirs first, then files, alpha) from the rows' relative paths, in one O(n) pass.
function buildTree(rows: FileRow[]): TreeNode[] {
  const root: TreeNode = { name: "", key: "", isDir: true, children: [] };
  const dirs = new Map<string, TreeNode>([["", root]]);
  for (const row of rows) {
    const segs = row.rel.split("/");
    let parent = root;
    let prefix = "";
    for (let i = 0; i < segs.length; i++) {
      const seg = segs[i] ?? "";
      const key = prefix === "" ? seg : `${prefix}/${seg}`;
      if (i === segs.length - 1) {
        parent.children?.push({ name: seg, key, isDir: false, abs: row.abs });
      } else {
        let dir = dirs.get(key);
        if (dir === undefined) {
          dir = { name: seg, key, isDir: true, children: [] };
          dirs.set(key, dir);
          parent.children?.push(dir);
        }
        parent = dir;
      }
      prefix = key;
    }
  }
  sortChildren(root);
  return root.children ?? [];
}

function sortChildren(node: TreeNode): void {
  if (node.children === undefined) {
    return;
  }
  node.children.sort((a, b) =>
    a.isDir !== b.isDir ? (a.isDir ? -1 : 1) : a.name.localeCompare(b.name),
  );
  for (const child of node.children) {
    sortChildren(child);
  }
}

// The expansion keys for every ancestor directory of a relative path (excludes the file leaf itself).
function ancestorKeys(rel: string): string[] {
  const segs = rel.split("/");
  const keys: string[] = [];
  let prefix = "";
  for (let i = 0; i < segs.length - 1; i++) {
    prefix = prefix === "" ? (segs[i] ?? "") : `${prefix}/${segs[i]}`;
    keys.push(prefix);
  }
  return keys;
}

// The center omnibar quick-open: file tree when the query is empty, fuzzy-ranked flat list when typing, and a
// command palette when the query leads with ">". Focusing it asks the host for the file index.
export function Omnibar(props: {
  files: string[];
  // A session switch invalidated the index and the new worktree's walk is still running — the empty list
  // means "loading", not "this worktree has no files".
  filesPending: boolean;
  root: string | null;
  currentFile: string | null;
  workspaceLabel: string;
  onOpenFile: (abs: string) => void;
  onRequestIndex: () => void;
  // The editor's Go-to-Symbol surface (query + live preview/commit), used by the @ / # modes.
  symbols: SymbolActions;
}): JSX.Element {
  const [query, setQuery] = createSignal("");
  const [open, setOpen] = createSignal(false);
  const [selected, setSelected] = createSignal(0);
  const [expanded, setExpanded] = createSignal<Set<string>>(new Set());
  let inputRef!: HTMLInputElement;
  let rootRef!: HTMLDivElement;
  let listRef: HTMLDivElement | undefined;

  // Element focused when the omnibar opened; restored on close so the focusin-derived `when`-context
  // (editorFocused/terminalFocused) and editor-gated chords like Ctrl+Tab keep matching. See App's onFocusIn.
  let priorFocus: HTMLElement | null = null;

  // The command catalog, kept live as the host pushes keybinding/catalog changes.
  const [commandList, setCommandList] = createSignal<CommandInfo[]>(getCommands());
  onCleanup(onCommandsChanged(() => setCommandList(getCommands())));

  const rows = createMemo<FileRow[]>(() => {
    const root = props.root ?? "";
    return props.files.map((abs) => splitPath(abs, root));
  });

  // The active mode, chosen by the query's leading char: ">" palette, "@" this-file symbols, "#" workspace
  // symbols, empty → the file tree, otherwise the fuzzy file list.
  type Mode = "command" | "docSymbol" | "wsSymbol" | "tree" | "search";
  const mode = createMemo<Mode>(() => {
    const q = query();
    if (q.startsWith(">")) return "command";
    if (q.startsWith("@")) return "docSymbol";
    if (q.startsWith("#")) return "wsSymbol";
    return q.trim().length === 0 ? "tree" : "search";
  });
  const commandMode = (): boolean => mode() === "command";
  const treeMode = (): boolean => mode() === "tree";
  const searchMode = (): boolean => mode() === "search";
  const docSymbolMode = (): boolean => mode() === "docSymbol";
  const wsSymbolMode = (): boolean => mode() === "wsSymbol";
  const symbolMode = (): boolean => docSymbolMode() || wsSymbolMode();

  // One fuzzy finder over the file index, rebuilt only when the index changes.
  const fileFinder = createMemo(() => createFileFinder(rows()));

  // The fuzzy-ranked file matches (search mode only; uncapped, best-first), carrying match positions.
  const filtered = createMemo<ScoredFile[]>(() => {
    if (!searchMode()) {
      return [];
    }
    return rankFiles(fileFinder(), query().trim(), recentFiles());
  });

  const view = createMemo<ScoredFile[]>(() => filtered().slice(0, VIEW_CAP));

  // Symbol modes (@ / #): the editor sources + ranks the symbols; this omnibar only renders and navigates. Active
  // only while open and in a symbol mode. reloadKey (currentFile) forces a document-symbol refetch on a file swap.
  const symbolSearch = createSymbolSearch({
    active: () =>
      !open() ? null : docSymbolMode() ? "docSymbol" : wsSymbolMode() ? "wsSymbol" : null,
    query: () => query().slice(1),
    reloadKey: () => props.currentFile,
    symbols: props.symbols,
  });
  const symbolView = createMemo(() => symbolSearch.view().slice(0, VIEW_CAP));

  // The visible tree rows: a depth-first walk emitting a row only when all its ancestors are expanded.
  const treeNodes = createMemo<TreeNode[]>(() => buildTree(rows()));
  const visibleRows = createMemo<TreeRow[]>(() => {
    if (!treeMode()) {
      return [];
    }
    const exp = expanded();
    const out: TreeRow[] = [];
    const walk = (nodes: TreeNode[], depth: number): void => {
      for (const node of nodes) {
        out.push({ node, depth });
        if (node.isDir && exp.has(node.key) && node.children !== undefined) {
          walk(node.children, depth + 1);
        }
        if (out.length >= VIEW_CAP) {
          return;
        }
      }
    };
    walk(treeNodes(), 0);
    return out.slice(0, VIEW_CAP);
  });

  interface ScoredCommand {
    cmd: CommandInfo;
    positions?: Set<number>;
  }

  // The palette: visible commands whose `when` passes, fuzzy-ranked (with positions) over the text after ">".
  const commandView = createMemo<ScoredCommand[]>(() => {
    if (!commandMode()) {
      return [];
    }
    // Evaluate `when` against the pane focused when the palette opened, not the omnibar input it now holds —
    // otherwise every focus-gated command (Copy/Paste, etc.) would be filtered out the moment the palette opens.
    const focus = paneFocusContext(priorFocus);
    const all = commandList().filter((c) => c.showInPalette && evaluateWhen(c.when, focus));
    const q = query().slice(1).trim();
    if (q.length === 0) {
      return [...all]
        .sort(
          (a, b) =>
            (a.category ?? "").localeCompare(b.category ?? "") || a.title.localeCompare(b.title),
        )
        .map((cmd) => ({ cmd }));
    }
    const fzf = new Fzf(all, {
      selector: (c) => [c.title, c.category ?? "", ...c.aliases].join(" "),
      tiebreakers: [byLengthAsc],
    });
    return fzf.find(q).map((r) => ({ cmd: r.item, positions: r.positions }));
  });

  const activeLen = (): number =>
    commandMode()
      ? commandView().length
      : symbolMode()
        ? symbolView().length
        : treeMode()
          ? visibleRows().length
          : view().length;
  const hiddenCount = (): number =>
    searchMode()
      ? Math.max(0, filtered().length - view().length)
      : symbolMode()
        ? Math.max(0, symbolSearch.view().length - symbolView().length)
        : 0;

  const scrollToSelected = (block: ScrollLogicalPosition): void => {
    (listRef?.children[selected()] as HTMLElement | undefined)?.scrollIntoView({ block });
  };

  // True while an open tree-mode session still needs to center on the current file — the first reveal usually
  // runs against an empty `rows()`, so the later file-index arrival finishes it.
  const [pendingReveal, setPendingReveal] = createSignal(false);

  // Expand the current file's folder chain and center the selection on it. Returns false when the current
  // file isn't in the index yet (host reply in flight); the caller re-attempts once `rows()` arrives.
  const focusCurrentInTree = (): boolean => {
    const cf = props.currentFile;
    let revealed = true;
    if (cf !== null) {
      const row = rows().find((r) => samePath(r.abs, cf));
      if (row !== undefined) {
        setExpanded(new Set(ancestorKeys(row.rel)));
      } else {
        revealed = false;
      }
    }
    queueMicrotask(() => {
      const idx =
        cf !== null
          ? visibleRows().findIndex((r) => r.node.abs !== undefined && samePath(r.node.abs, cf))
          : -1;
      setSelected(idx >= 0 ? idx : 0);
      scrollToSelected("center");
    });
    return revealed;
  };

  // On open: command mode → top; file mode → reveal+center the current file in the tree.
  createEffect(
    on(open, (isOpen) => {
      if (!isOpen) {
        setPendingReveal(false);
        return;
      }
      if (commandMode() || symbolMode()) {
        setSelected(0);
        return;
      }
      setPendingReveal(!focusCurrentInTree());
    }),
  );

  // Finish the reveal once the async file index lands, then stop so later manual expand/collapse stands.
  createEffect(
    on(
      rows,
      () => {
        if (open() && treeMode() && pendingReveal()) {
          focusCurrentInTree();
          setPendingReveal(false);
        }
      },
      { defer: true },
    ),
  );

  // On query change: empty file query re-reveals the current file; otherwise reset to the top.
  createEffect(
    on(
      query,
      () => {
        if (treeMode()) {
          setPendingReveal(!focusCurrentInTree());
        } else {
          setSelected(0);
          queueMicrotask(() => scrollToSelected("nearest"));
        }
      },
      { defer: true },
    ),
  );

  // Leaving symbol mode (deleting the @/#, or the omnibar closing, which resets the query) without committing
  // restores the editor to where the preview started.
  createEffect(
    on(symbolMode, (isSymbol, wasSymbol) => {
      if (wasSymbol && !isSymbol) {
        props.symbols.cancelPreview();
      }
    }),
  );

  // A focus-omnibar command opened us: switch to the requested mode, focus the input, refresh the index.
  createEffect(
    on(
      omnibarRequest,
      (request) => {
        if (request === null) {
          return;
        }
        setQuery(MODE_PREFIX[request.mode]);
        // Capture the element we're stealing focus from BEFORE focusing the input: a programmatic focus()
        // delivers a null relatedTarget, so the input's onFocus can't record it, and close would drop focus.
        const active = document.activeElement as HTMLElement | null;
        if (active !== null && active !== document.body && !rootRef.contains(active)) {
          priorFocus = active;
        }
        setOpen(true);
        props.onRequestIndex();
        queueMicrotask(() => inputRef.focus());
      },
      { defer: true },
    ),
  );

  // Return focus to wherever it was before opening, restoring its `when`-context; falls back to blurring
  // the input when there's nothing valid to return to.
  const restorePriorFocus = (): void => {
    const target = priorFocus;
    priorFocus = null;
    if (target?.isConnected && target !== document.body) {
      target.focus();
    } else {
      inputRef.blur();
    }
  };

  const close = (): void => {
    setOpen(false);
    setQuery("");
    restorePriorFocus();
  };

  // Dismiss because focus left the omnibar (Tab-away or an outside click): close WITHOUT grabbing focus back —
  // it belongs wherever the user moved it. Resets state so a reopen starts clean (the old path leaked query +
  // priorFocus and used an uncleared timer).
  const dismiss = (): void => {
    setOpen(false);
    setQuery("");
    priorFocus = null;
  };

  const openFile = (abs: string | undefined): void => {
    if (abs === undefined) {
      return;
    }
    // Canonical (lowercase-drive) form the editor keys working copies by, so an already-open file is reused
    // instead of opening a second editor. See editor/fs-path.ts.
    props.onOpenFile(canonicalFsPath(abs));
    close();
  };

  const runCommand = (cmd: CommandInfo | undefined): void => {
    if (cmd === undefined) {
      return;
    }
    // The omnibar's own focus commands just re-aim it at a mode — do that in place (the input already has focus)
    // rather than close()+re-open, which races the reset and lands in plain file mode.
    const focusMode = FOCUS_COMMAND_MODE[cmd.id];
    if (focusMode !== undefined) {
      setQuery(MODE_PREFIX[focusMode]);
      setSelected(0);
      return;
    }
    void runCommandWithFeedback(cmd.id);
    close();
  };

  const activateSymbol = (sym: FlatSymbol | undefined): void => {
    if (sym === undefined) {
      return;
    }
    props.symbols.commitPreview(sym);
    close();
  };

  const toggleDir = (key: string): void => {
    setExpanded((s) => {
      const next = new Set(s);
      if (next.has(key)) {
        next.delete(key);
      } else {
        next.add(key);
      }
      return next;
    });
    // The visible list grew/shrank — keep the selection in range.
    queueMicrotask(() => setSelected((i) => Math.min(i, Math.max(0, visibleRows().length - 1))));
  };

  // Left/Right move a full level at a time. Right: expand a collapsed dir, else skip to the next row at the
  // same-or-shallower depth. Left: collapse an expanded dir, else jump up to the parent row.
  const treeMoveLevel = (dir: 1 | -1): void => {
    const rowsV = visibleRows();
    const i = selected();
    const cur = rowsV[i];
    if (cur === undefined) {
      return;
    }
    if (dir === 1) {
      if (cur.node.isDir && !expanded().has(cur.node.key)) {
        toggleDir(cur.node.key);
        return;
      }
      for (let j = i + 1; j < rowsV.length; j++) {
        if ((rowsV[j]?.depth ?? 0) <= cur.depth) {
          setSelected(j);
          scrollToSelected("nearest");
          return;
        }
      }
      setSelected(rowsV.length - 1);
    } else {
      if (cur.node.isDir && expanded().has(cur.node.key)) {
        toggleDir(cur.node.key);
        return;
      }
      for (let j = i - 1; j >= 0; j--) {
        if ((rowsV[j]?.depth ?? 0) < cur.depth) {
          setSelected(j);
          scrollToSelected("nearest");
          return;
        }
      }
      setSelected(0);
    }
    scrollToSelected("nearest");
  };

  const activate = (): void => {
    if (commandMode()) {
      runCommand(commandView()[selected()]?.cmd);
    } else if (symbolMode()) {
      activateSymbol(symbolView()[selected()]?.sym);
    } else if (treeMode()) {
      const r = visibleRows()[selected()];
      if (r === undefined) {
        return;
      }
      if (r.node.isDir) {
        toggleDir(r.node.key);
      } else {
        openFile(r.node.abs);
      }
    } else {
      openFile(view()[selected()]?.row.abs);
    }
  };

  // Live-preview the selected symbol in the real editor — driven ONLY by explicit arrow navigation, never by
  // opening the omnibar or typing to filter, so searching for a symbol never yanks the editor off the user's
  // spot. The reveal is same-file only (see the editor's previewSymbol); Esc restores the pre-preview view.
  const previewSelected = (): void => {
    if (symbolMode()) {
      const sym = symbolView()[selected()]?.sym;
      if (sym !== undefined) {
        props.symbols.preview(sym);
      }
    }
  };

  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setSelected((i) => Math.min(i + 1, activeLen() - 1));
      scrollToSelected("nearest");
      previewSelected();
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setSelected((i) => Math.max(i - 1, 0));
      scrollToSelected("nearest");
      previewSelected();
    } else if (e.key === "ArrowRight" && treeMode()) {
      e.preventDefault();
      treeMoveLevel(1);
    } else if (e.key === "ArrowLeft" && treeMode()) {
      e.preventDefault();
      treeMoveLevel(-1);
    } else if (e.key === "Enter") {
      e.preventDefault();
      activate();
    } else if (e.key === "Escape") {
      e.preventDefault();
      close();
    }
  };

  // Keyboard focus moved to a real element outside the omnibar (Tab-away): dismiss. A null relatedTarget is a
  // mouse blur (clicking elsewhere) — left to the pointer-down-outside listener, which distinguishes a row
  // click from an outside click without the old settle timer.
  const onFocusOut = (e: FocusEvent): void => {
    const next = e.relatedTarget as Node | null;
    if (next !== null && !(e.currentTarget as HTMLElement).contains(next)) {
      dismiss();
    }
  };

  // Mouse dismiss: a pointer-down anywhere outside the open omnibar closes it (capture phase, so it lands
  // before the click activates whatever was hit). A click on a row is inside rootRef, so it's never caught.
  const onPointerDownOutside = (e: PointerEvent): void => {
    if (open() && !rootRef.contains(e.target as Node)) {
      dismiss();
    }
  };
  window.addEventListener("pointerdown", onPointerDownOutside, true);
  onCleanup(() => window.removeEventListener("pointerdown", onPointerDownOutside, true));

  // A glyph per symbol kind (see symbol-source's kindLabel), falling back to a generic mark.
  const kindIcon = (kind: string): JSX.Element => {
    switch (kind) {
      case "class":
      case "struct":
      case "interface":
      case "enum":
      case "module":
        return <Box />;
      case "method":
      case "function":
      case "constructor":
        return <Braces />;
      case "property":
      case "field":
      case "variable":
      case "constant":
      case "enum-member":
        return <Variable />;
      case "type":
        return <Type />;
      default:
        return <Hash />;
    }
  };

  // The dimmed context after a symbol name: its container chain, plus the file for workspace symbols (which span
  // the repo, so the row is ambiguous without it).
  const symbolDir = (sym: FlatSymbol): string => {
    if (!wsSymbolMode()) {
      return sym.container;
    }
    const rel = splitPath(sym.path, props.root ?? "").rel;
    return sym.container !== "" ? `${sym.container} · ${rel}` : rel;
  };

  // The honest empty/loading/no-provider line — never a silent blank list (no-fallbacks rule).
  const symbolEmptyText = (): string => {
    switch (symbolSearch.status()) {
      case "loading":
        return docSymbolMode() ? "Loading symbols…" : "Searching…";
      case "idle":
        return "Type to search workspace symbols";
      case "noProvider":
        return docSymbolMode()
          ? "No symbols for this file"
          : "No workspace symbol provider — is the language server running?";
      default:
        return query().slice(1).trim().length > 0
          ? "No matching symbols"
          : "No symbols in this file";
    }
  };

  return (
    <div class="tb-omnibar" ref={rootRef} onFocusOut={onFocusOut}>
      <div class="tb-omnibar-box" classList={{ open: open() }}>
        <span class="tb-omnibar-icon" aria-hidden="true">
          <Search />
        </span>
        <input
          ref={inputRef}
          class="tb-omnibar-input"
          type="text"
          role="combobox"
          aria-label={
            commandMode()
              ? "Command palette"
              : docSymbolMode()
                ? "Go to symbol in file"
                : wsSymbolMode()
                  ? "Go to symbol in workspace"
                  : "Go to file"
          }
          aria-expanded={open() && activeLen() > 0}
          aria-controls={open() && activeLen() > 0 ? "tb-omnibar-listbox" : undefined}
          aria-activedescendant={
            open() && activeLen() > 0 ? `tb-omnibar-opt-${selected()}` : undefined
          }
          aria-autocomplete="list"
          spellcheck={false}
          placeholder={props.workspaceLabel}
          value={query()}
          onInput={(e) => setQuery(e.currentTarget.value)}
          onFocus={(e) => {
            // Remember the element we're stealing focus from so close can hand it back. Ignore a target
            // inside the omnibar itself so re-entry never overwrites it.
            const from = e.relatedTarget as HTMLElement | null;
            if (from !== null && !rootRef.contains(from)) {
              priorFocus = from;
            }
            setOpen(true);
            props.onRequestIndex();
          }}
          onClick={() => setOpen(true)}
          onKeyDown={onKeyDown}
        />
      </div>
      <Show when={open()}>
        <div class="tb-omnibar-pop" classList={{ symbol: symbolMode() }}>
          <Show when={symbolMode()}>
            <Show
              when={symbolView().length > 0}
              fallback={<div class="tb-omnibar-empty">{symbolEmptyText()}</div>}
            >
              <div
                class="tb-omnibar-list"
                ref={listRef}
                id="tb-omnibar-listbox"
                role="listbox"
                aria-label="Symbols"
              >
                <For each={symbolView()}>
                  {(item, i) => (
                    <button
                      type="button"
                      class="tb-omnibar-row tb-symbol-row"
                      role="option"
                      tabindex={-1}
                      id={`tb-omnibar-opt-${i()}`}
                      aria-selected={i() === selected()}
                      classList={{ selected: i() === selected() }}
                      onMouseDown={(e) => {
                        // mousedown fires before the input's focusout closes the popover.
                        e.preventDefault();
                        setSelected(i());
                        activateSymbol(item.sym);
                      }}
                    >
                      <span class="tb-symbol-kind" aria-hidden="true">
                        {kindIcon(item.sym.kind)}
                      </span>
                      <span class="tb-row-leaf">
                        {highlightSlice(item.sym.name, item.positions, 0)}
                      </span>
                      <Show when={symbolDir(item.sym).length > 0}>
                        <span class="tb-row-dir">{symbolDir(item.sym)}</span>
                      </Show>
                    </button>
                  )}
                </For>
              </div>
              <Show when={hiddenCount() > 0}>
                <div class="tb-omnibar-more">+{hiddenCount()} more — type to filter</div>
              </Show>
            </Show>
          </Show>
          <Show when={!symbolMode()}>
            <Show
              when={!commandMode()}
              fallback={
                <Show
                  when={commandView().length > 0}
                  fallback={<div class="tb-omnibar-empty">No matching commands</div>}
                >
                  <div
                    class="tb-omnibar-list"
                    ref={listRef}
                    id="tb-omnibar-listbox"
                    role="listbox"
                    aria-label="Commands"
                  >
                    <For each={commandView()}>
                      {(item, i) => (
                        <button
                          type="button"
                          class="tb-omnibar-row"
                          role="option"
                          tabindex={-1}
                          id={`tb-omnibar-opt-${i()}`}
                          aria-selected={i() === selected()}
                          classList={{ selected: i() === selected() }}
                          onMouseDown={(e) => {
                            e.preventDefault();
                            setSelected(i());
                            runCommand(item.cmd);
                          }}
                        >
                          <span class="tb-row-leaf">
                            {highlightSlice(item.cmd.title, item.positions, 0)}
                          </span>
                          <Show when={item.cmd.category}>
                            <span class="tb-row-dir">{item.cmd.category}</span>
                          </Show>
                          <Show when={item.cmd.keys.length > 0}>
                            <span class="tb-row-keys">
                              {item.cmd.keys.map(formatKey).join(" / ")}
                            </span>
                          </Show>
                        </button>
                      )}
                    </For>
                  </div>
                </Show>
              }
            >
              <Show
                when={treeMode()}
                fallback={
                  <Show
                    when={view().length > 0}
                    fallback={
                      <div class="tb-omnibar-empty">
                        {props.filesPending ? "Loading files…" : "No matching files"}
                      </div>
                    }
                  >
                    <div
                      class="tb-omnibar-list"
                      ref={listRef}
                      id="tb-omnibar-listbox"
                      role="listbox"
                      aria-label="Files"
                    >
                      <For each={view()}>
                        {(item, i) => (
                          <button
                            type="button"
                            class="tb-omnibar-row"
                            role="option"
                            tabindex={-1}
                            id={`tb-omnibar-opt-${i()}`}
                            aria-selected={i() === selected()}
                            classList={{
                              selected: i() === selected(),
                              current:
                                props.currentFile !== null &&
                                samePath(item.row.abs, props.currentFile),
                            }}
                            onMouseDown={(e) => {
                              // mousedown fires before the input's focusout closes the popover.
                              e.preventDefault();
                              setSelected(i());
                              openFile(item.row.abs);
                            }}
                          >
                            <span class="tb-row-leaf">
                              {highlightSlice(item.row.leaf, item.positions, item.row.leafStart)}
                            </span>
                            <Show when={item.row.dir.length > 0}>
                              <span class="tb-row-dir">
                                {highlightSlice(item.row.dir, item.positions, 0)}
                              </span>
                            </Show>
                          </button>
                        )}
                      </For>
                    </div>
                    <Show when={hiddenCount() > 0}>
                      <div class="tb-omnibar-more">+{hiddenCount()} more — type to filter</div>
                    </Show>
                  </Show>
                }
              >
                <Show
                  when={visibleRows().length > 0}
                  fallback={
                    <div class="tb-omnibar-empty">
                      {props.filesPending ? "Loading files…" : "No files"}
                    </div>
                  }
                >
                  <div
                    class="tb-omnibar-list"
                    ref={listRef}
                    id="tb-omnibar-listbox"
                    role="listbox"
                    aria-label="Files"
                  >
                    <For each={visibleRows()}>
                      {(r, i) => (
                        <button
                          type="button"
                          class="tb-omnibar-row tb-tree-row"
                          role="option"
                          tabindex={-1}
                          id={`tb-omnibar-opt-${i()}`}
                          aria-selected={i() === selected()}
                          classList={{
                            dir: r.node.isDir,
                            selected: i() === selected(),
                            current:
                              props.currentFile !== null &&
                              r.node.abs !== undefined &&
                              samePath(r.node.abs, props.currentFile),
                          }}
                          style={`padding-left: ${10 + r.depth * 14}px`}
                          onMouseDown={(e) => {
                            e.preventDefault();
                            setSelected(i());
                            if (r.node.isDir) {
                              toggleDir(r.node.key);
                            } else {
                              openFile(r.node.abs);
                            }
                          }}
                        >
                          <span class="tb-tree-twisty" aria-hidden="true">
                            <Show when={r.node.isDir}>
                              <Show when={expanded().has(r.node.key)} fallback={<ChevronRight />}>
                                <ChevronDown />
                              </Show>
                            </Show>
                          </span>
                          <span class="tb-tree-icon" aria-hidden="true">
                            <Show when={r.node.isDir} fallback={<FileIcon />}>
                              <Show when={expanded().has(r.node.key)} fallback={<Folder />}>
                                <FolderOpen />
                              </Show>
                            </Show>
                          </span>
                          <span class="tb-row-leaf">{r.node.name}</span>
                        </button>
                      )}
                    </For>
                  </div>
                </Show>
              </Show>
            </Show>
          </Show>
        </div>
      </Show>
    </div>
  );
}
