import { byLengthAsc, Fzf } from "fzf";
import { Search } from "lucide-solid";
import {
  createEffect,
  createMemo,
  createSignal,
  type JSX,
  on,
  onCleanup,
  Show,
  untrack,
} from "solid-js";
import { selectedSession } from "../bridge";
import { evaluateWhen, paneFocusContext } from "../commands/context";
import { getCommands, onCommandsChanged, runCommandWithFeedback } from "../commands/registry";
import { CommandIds, type CommandInfo } from "../commands/types";
import { canonicalFsPath, samePath } from "../editor/fs-path";
import type { DirEntry } from "../files/FileBrowser";
import {
  buildPathTree,
  type PathTreeNode,
  type PathTreeRow,
  pathAncestorKeys,
  visiblePathTreeRows,
} from "../files/path-tree";
import {
  listSelectedDirectory,
  selectedDirectoryListings,
  selectedFileIndex,
} from "../files/session-files";
import { createListNavigation } from "../list-navigation";
import type { FlatSymbol, SymbolActions } from "../symbols/symbol-match";
import { createSymbolSearch } from "../symbols/symbol-search";
import {
  activeDir,
  createFileFinder,
  type FileRow,
  rankFiles,
  type ScoredFile,
  splitPath,
} from "./file-search";
import { onModalOpened } from "./modal-state";
import { OmnibarResults, type ScoredCommand } from "./OmnibarResults";
import { type OmnibarMode, omnibarRequest } from "./omnibar-controller";
import { parsePathQuery, pathSeed, separatorFor } from "./path-query";
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
  onOpenFile: (abs: string, line: number | undefined) => void;
  onRequestIndex: () => void;
  // The editor's Go-to-Symbol surface (query + live preview/commit), used by the @ / # modes.
  symbols: SymbolActions;
}): JSX.Element {
  const [query, setQuery] = createSignal("");
  const [open, setOpen] = createSignal(false);
  const nav = createListNavigation({
    count: () => activeLen(),
    edges: "clamp",
    initialIndex: 0,
    acceptKeys: ["Enter"],
    onAccept: () => activate(),
    onDismiss: () => close(),
    onMove: () => {
      scrollToSelected("nearest");
      previewSelected();
    },
    consumeEmptyArrows: true,
  });
  // Aliased: the selection is read and re-homed all through this file, not just by the keyboard.
  const selected = nav.index;
  const setSelected = nav.setIndex;
  const [expanded, setExpanded] = createSignal<Set<string>>(new Set());
  let inputRef!: HTMLInputElement;
  let rootRef!: HTMLDivElement;
  let listRef: HTMLDivElement | undefined;

  // Element focused when the omnibar opened; restored on close so the focusin-derived `when`-context
  // (editorFocused/terminalFocused) and editor-gated chords like Ctrl+Tab keep matching. See App's onFocusIn.
  let priorFocus: HTMLElement | null = null;

  // The 1-based line an open from this omnibar session reveals — a host-driven request resolving an
  // ambiguous `file:line` link carries the link's line, applied to whichever candidate the user picks.
  // Undefined for a plain quick-open, which leaves an already-open tab where the user left it.
  let pendingLine: number | undefined;

  // The command catalog, kept live as the host pushes keybinding/catalog changes.
  const [commandList, setCommandList] = createSignal<CommandInfo[]>(getCommands());
  onCleanup(onCommandsChanged(() => setCommandList(getCommands())));

  const rows = createMemo<FileRow[]>(() => {
    const root = props.root ?? "";
    return props.files.map((abs) => splitPath(abs, root));
  });

  // The active mode, chosen by the query's leading char: ">" palette, "@" this-file symbols, "#" workspace
  // symbols, empty → the file tree, otherwise the fuzzy file list.
  type Mode = "command" | "docSymbol" | "wsSymbol" | "tree" | "search" | "path";
  const pathQuery = createMemo(() =>
    parsePathQuery(query(), { root: props.root ?? "", home: selectedFileIndex().home }),
  );
  const mode = createMemo<Mode>(() => {
    const q = query();
    if (q.startsWith(">")) return "command";
    if (q.startsWith("@")) return "docSymbol";
    if (q.startsWith("#")) return "wsSymbol";
    if (pathQuery() !== null) return "path";
    return q.trim().length === 0 ? "tree" : "search";
  });
  const commandMode = (): boolean => mode() === "command";
  const treeMode = (): boolean => mode() === "tree";
  const searchMode = (): boolean => mode() === "search";
  const docSymbolMode = (): boolean => mode() === "docSymbol";
  const wsSymbolMode = (): boolean => mode() === "wsSymbol";
  const pathMode = (): boolean => mode() === "path";
  const symbolMode = (): boolean => docSymbolMode() || wsSymbolMode();

  // Tracks the directory and the session, so switching sessions (which clears that session's listings)
  // re-requests rather than waiting forever on a listing nobody will send. The request itself is untracked
  // because it reads the listings store to dedupe, and tracking that would make a reply re-trigger it.
  const pathDir = createMemo(() => pathQuery()?.dir ?? null);
  createEffect(() => {
    const dir = pathDir();
    const session = selectedSession();
    if (dir !== null && session !== null) {
      untrack(() => listSelectedDirectory(dir));
    }
  });
  const pathListing = () => {
    const dir = pathDir();
    return dir === null ? undefined : selectedDirectoryListings()[dir];
  };
  // A directory that can't be listed says why — an empty row list would read as an empty directory.
  const pathError = (): string | null => {
    const listing = pathListing();
    return listing?.status === "error" ? listing.message : null;
  };
  const pathRows = createMemo<DirEntry[]>(() => {
    const parsed = pathQuery();
    const listing = pathListing();
    if (parsed === null || listing?.status !== "ready") {
      return [];
    }
    const leaf = parsed.leaf.toLowerCase();
    return listing.entries.filter((entry) => entry.name.toLowerCase().startsWith(leaf));
  });
  const pathView = createMemo(() => pathRows().slice(0, VIEW_CAP));

  // One fuzzy finder over the file index, rebuilt only when the index changes.
  const fileFinder = createMemo(() => createFileFinder(rows()));

  // The fuzzy-ranked file matches (search mode only; uncapped, best-first), carrying match positions.
  const filtered = createMemo<ScoredFile[]>(() => {
    if (!searchMode()) {
      return [];
    }
    return rankFiles(
      fileFinder(),
      query().trim(),
      recentFiles(),
      activeDir(props.currentFile, props.root ?? ""),
    );
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
  const treeNodes = createMemo<PathTreeNode<string>[]>(() =>
    buildPathTree(rows().map((row) => ({ path: row.rel, value: row.abs }))),
  );
  const visibleRows = createMemo<PathTreeRow<string>[]>(() => {
    if (!treeMode()) {
      return [];
    }
    return visiblePathTreeRows(treeNodes(), expanded(), VIEW_CAP);
  });

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
    pathMode()
      ? pathView().length
      : commandMode()
        ? commandView().length
        : symbolMode()
          ? symbolView().length
          : treeMode()
            ? visibleRows().length
            : view().length;
  const hiddenCount = (): number =>
    pathMode()
      ? Math.max(0, pathRows().length - pathView().length)
      : searchMode()
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
        setExpanded(new Set(pathAncestorKeys(row.rel)));
      } else {
        revealed = false;
      }
    }
    queueMicrotask(() => {
      const idx =
        cf !== null
          ? visibleRows().findIndex((r) => r.node.kind === "file" && samePath(r.node.value, cf))
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
        setQuery(MODE_PREFIX[request.mode] + request.query);
        pendingLine = request.line;
        // Capture the element we're stealing focus from BEFORE focusing the input: a programmatic focus()
        // delivers a null relatedTarget, so the input's onFocus can't record it, and close would drop focus.
        const active = document.activeElement as HTMLElement | null;
        if (active !== null && active !== document.body && !rootRef.contains(active)) {
          priorFocus = active;
        }
        setOpen(true);
        props.onRequestIndex();
        queueMicrotask(() => {
          inputRef.focus();
          // A preloaded query (an ambiguous link's recovery search) is selected so typing replaces it; a bare
          // mode prefix (">") or an open-by-path seed must never be, or the first keystroke would wipe it.
          if (request.select && request.query !== "") {
            inputRef.select();
          }
        });
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
    pendingLine = undefined;
    restorePriorFocus();
  };

  // Dismiss because focus left the omnibar (Tab-away or an outside click): close WITHOUT grabbing focus back —
  // it belongs wherever the user moved it. Resets state so a reopen starts clean (the old path leaked query +
  // priorFocus and used an uncleared timer).
  const dismiss = (): void => {
    setOpen(false);
    setQuery("");
    pendingLine = undefined;
    priorFocus = null;
  };
  onCleanup(onModalOpened(dismiss));

  const openFile = (abs: string | undefined): void => {
    if (abs === undefined) {
      return;
    }
    // Canonical (lowercase-drive) form the editor keys working copies by, so an already-open file is reused
    // instead of opening a second editor. See editor/fs-path.ts.
    props.onOpenFile(canonicalFsPath(abs), pendingLine);
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
    // Open-by-path re-aims in place too, but its seed is the worktree root rather than a static prefix.
    if (cmd.id === CommandIds.openFileByPath) {
      setQuery(pathSeed(props.root ?? ""));
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
      if (cur.node.kind === "directory" && !expanded().has(cur.node.key)) {
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
      if (cur.node.kind === "directory" && expanded().has(cur.node.key)) {
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

  const activatePathEntry = (entry: DirEntry | undefined): void => {
    if (entry === undefined) {
      return;
    }
    if (entry.isDir) {
      setQuery(entry.path + separatorFor(entry.path));
      setSelected(0);
    } else {
      openFile(entry.path);
    }
  };

  // A fully-typed path opens itself. An exact filename beats the highlighted row, because the host sorts
  // directories first and a sibling directory sharing the prefix would otherwise win; and with no rows yet
  // (the listing is still in flight, or the path is wrong) the opener decides — it gates on existence and
  // toasts when it can't open, so a pasted path never lands on a silently dead Enter.
  const activatePath = (): void => {
    const parsed = pathQuery();
    if (parsed === null) {
      return;
    }
    const rows = pathView();
    const exact = rows.find((entry) => !entry.isDir && entry.name === parsed.leaf);
    if (exact !== undefined) {
      openFile(exact.path);
    } else if (rows.length === 0 && parsed.leaf !== "") {
      openFile(parsed.absolute);
    } else {
      activatePathEntry(rows[selected()]);
    }
  };

  const activate = (): void => {
    if (pathMode()) {
      activatePath();
    } else if (commandMode()) {
      runCommand(commandView()[selected()]?.cmd);
    } else if (symbolMode()) {
      activateSymbol(symbolView()[selected()]?.sym);
    } else if (treeMode()) {
      const r = visibleRows()[selected()];
      if (r === undefined) {
        return;
      }
      if (r.node.kind === "directory") {
        toggleDir(r.node.key);
      } else {
        openFile(r.node.value);
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
    if (nav.onKeyDown(e)) {
      return;
    }
    if ((e.key === "ArrowRight" || e.key === "ArrowLeft") && treeMode()) {
      e.preventDefault();
      treeMoveLevel(e.key === "ArrowRight" ? 1 : -1);
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
      case "error":
        return "Symbol search failed — check the diagnostics log.";
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
          <OmnibarResults
            mode={mode}
            selected={selected}
            onSelect={setSelected}
            listRef={(element) => {
              listRef = element;
            }}
            hiddenCount={hiddenCount}
            filesPending={props.filesPending}
            currentFile={props.currentFile}
            pathRows={pathView}
            pathError={pathError}
            pathReady={() => pathListing()?.status === "ready"}
            onActivatePath={activatePathEntry}
            symbolRows={symbolView}
            symbolEmptyText={symbolEmptyText}
            symbolDir={symbolDir}
            onActivateSymbol={activateSymbol}
            commandRows={commandView}
            onRunCommand={runCommand}
            fileRows={view}
            treeRows={visibleRows}
            expanded={expanded}
            onOpenFile={openFile}
            onToggleDir={toggleDir}
          />
        </div>
      </Show>
    </div>
  );
}
