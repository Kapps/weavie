import { Fzf, byLengthAsc } from "fzf";
import {
  ChevronDown,
  ChevronRight,
  File as FileIcon,
  Folder,
  FolderOpen,
  Search,
} from "lucide-solid";
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
import { canonicalFsPath, samePath } from "../editor/fs-path";
import { type FileRow, type ScoredFile, createFileFinder, rankFiles } from "./file-search";
import { highlightSlice } from "./highlight";
import { omnibarRequest } from "./omnibar-controller";
import { recentFiles } from "./recent-files-store";

// Max rows rendered at once — a safety cap so a giant workspace never mounts thousands of rows.
const VIEW_CAP = 300;

function splitPath(abs: string, root: string): FileRow {
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
    leafStart: slash >= 0 ? slash + 1 : 0,
  };
}

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
  root: string | null;
  currentFile: string | null;
  workspaceLabel: string;
  onOpenFile: (abs: string) => void;
  onRequestIndex: () => void;
}): JSX.Element {
  const [query, setQuery] = createSignal("");
  const [open, setOpen] = createSignal(false);
  const [selected, setSelected] = createSignal(0);
  const [expanded, setExpanded] = createSignal<Set<string>>(new Set());
  let inputRef!: HTMLInputElement;
  let rootRef!: HTMLDivElement;
  let listRef: HTMLUListElement | undefined;

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

  const commandMode = (): boolean => query().startsWith(">");
  // Empty file query → the tree; with text → the flat ranked list.
  const treeMode = (): boolean => !commandMode() && query().trim().length === 0;
  const searchMode = (): boolean => !commandMode() && query().trim().length > 0;

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
    const all = commandList().filter((c) => c.showInPalette && evaluateWhen(c.when));
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
    commandMode() ? commandView().length : treeMode() ? visibleRows().length : view().length;
  const hiddenCount = (): number =>
    searchMode() ? Math.max(0, filtered().length - view().length) : 0;

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
      if (commandMode()) {
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
    dispatchCommand(cmd.id);
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

  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setSelected((i) => Math.min(i + 1, activeLen() - 1));
      scrollToSelected("nearest");
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setSelected((i) => Math.max(i - 1, 0));
      scrollToSelected("nearest");
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

  // Close when focus leaves the omnibar entirely; a short delay lets a row's click register first.
  const onFocusOut = (e: FocusEvent): void => {
    const next = e.relatedTarget as Node | null;
    if (next === null || !(e.currentTarget as HTMLElement).contains(next)) {
      window.setTimeout(() => setOpen(false), 120);
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
                    {(item, i) => (
                      <li>
                        <button
                          type="button"
                          class="tb-omnibar-row"
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
                      </li>
                    )}
                  </For>
                </ul>
              </Show>
            }
          >
            <Show
              when={treeMode()}
              fallback={
                <Show
                  when={view().length > 0}
                  fallback={<div class="tb-omnibar-empty">No matching files</div>}
                >
                  <ul class="tb-omnibar-list" ref={listRef}>
                    <For each={view()}>
                      {(item, i) => (
                        <li>
                          <button
                            type="button"
                            class="tb-omnibar-row"
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
                        </li>
                      )}
                    </For>
                  </ul>
                  <Show when={hiddenCount() > 0}>
                    <div class="tb-omnibar-more">+{hiddenCount()} more — type to filter</div>
                  </Show>
                </Show>
              }
            >
              <Show
                when={visibleRows().length > 0}
                fallback={<div class="tb-omnibar-empty">No files</div>}
              >
                <ul class="tb-omnibar-list" ref={listRef}>
                  <For each={visibleRows()}>
                    {(r, i) => (
                      <li>
                        <button
                          type="button"
                          class="tb-omnibar-row tb-tree-row"
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
                      </li>
                    )}
                  </For>
                </ul>
              </Show>
            </Show>
          </Show>
        </div>
      </Show>
    </div>
  );
}
