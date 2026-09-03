import {
  Box,
  Braces,
  ChevronDown,
  ChevronRight,
  File as FileIcon,
  Folder,
  FolderOpen,
  Hash,
  Type,
  Variable,
} from "lucide-solid";
import { For, type JSX, Match, Show, Switch } from "solid-js";
import { formatKey } from "../commands/keybindings";
import type { CommandInfo } from "../commands/types";
import { samePath } from "../editor/fs-path";
import type { DirEntry } from "../files/FileBrowser";
import type { FlatSymbol, ScoredSymbol } from "../symbols/symbol-match";
import type { ScoredFile } from "./file-search";
import { highlightSlice } from "./highlight";

/** A node in the client-side file tree. `key` (the dir's relative path) is the expansion-state key. */
export interface TreeNode {
  name: string;
  key: string;
  isDir: boolean;
  abs?: string;
  children?: TreeNode[];
}

/** One rendered tree line: the node and how deep it sits. */
export interface TreeRow {
  node: TreeNode;
  depth: number;
}

/** One command row: the command and the query positions to highlight in its title. */
export interface ScoredCommand {
  cmd: CommandInfo;
  positions?: Set<number>;
}

/** The mode whose rows are on screen; each is one branch below. */
export type OmnibarResultMode = "command" | "docSymbol" | "wsSymbol" | "tree" | "search" | "path";

/**
 * The omnibar's result rows. Owns only rendering and mouse activation: the Omnibar keeps the input, the mode
 * decision, selection, and the keyboard, and hands each mode its already-capped rows. Splitting here keeps
 * adding a mode from growing the file that also owns focus and key handling.
 */
export function OmnibarResults(props: {
  mode: () => OmnibarResultMode;
  selected: () => number;
  onSelect: (index: number) => void;
  listRef: (element: HTMLDivElement) => void;
  hiddenCount: () => number;
  filesPending: boolean;
  currentFile: string | null;

  pathRows: () => DirEntry[];
  pathError: () => string | null;
  pathReady: () => boolean;
  onActivatePath: (entry: DirEntry) => void;

  symbolRows: () => ScoredSymbol[];
  symbolEmptyText: () => string;
  symbolDir: (symbol: FlatSymbol) => string;
  onActivateSymbol: (symbol: FlatSymbol) => void;

  commandRows: () => ScoredCommand[];
  onRunCommand: (command: CommandInfo) => void;

  fileRows: () => ScoredFile[];
  treeRows: () => TreeRow[];
  expanded: () => Set<string>;
  onOpenFile: (absolute: string | undefined) => void;
  onToggleDir: (key: string) => void;
}): JSX.Element {
  // mousedown fires before the input's focusout closes the popover, so every row activates on it.
  const press = (index: number, run: () => void) => (event: MouseEvent) => {
    event.preventDefault();
    props.onSelect(index);
    run();
  };
  const rowAttributes = (index: number): JSX.ButtonHTMLAttributes<HTMLButtonElement> => ({
    type: "button",
    role: "option",
    tabindex: -1,
    id: `tb-omnibar-opt-${index}`,
    "aria-selected": index === props.selected(),
  });
  const isCurrent = (absolute: string | undefined): boolean =>
    props.currentFile !== null && absolute !== undefined && samePath(absolute, props.currentFile);
  const List = (listProps: { label: string; children: JSX.Element }): JSX.Element => (
    <div
      class="tb-omnibar-list"
      ref={props.listRef}
      id="tb-omnibar-listbox"
      role="listbox"
      aria-label={listProps.label}
    >
      {listProps.children}
    </div>
  );
  const More = (): JSX.Element => (
    <Show when={props.hiddenCount() > 0}>
      <div class="tb-omnibar-more">+{props.hiddenCount()} more — type to filter</div>
    </Show>
  );
  const Empty = (emptyProps: { children: JSX.Element }): JSX.Element => (
    <div class="tb-omnibar-empty">{emptyProps.children}</div>
  );

  return (
    <Switch>
      <Match when={props.mode() === "path"}>
        <Show when={props.pathError()} fallback={<PathRows />}>
          {(message) => <Empty>{message()}</Empty>}
        </Show>
      </Match>
      <Match when={props.mode() === "docSymbol" || props.mode() === "wsSymbol"}>
        <Show
          when={props.symbolRows().length > 0}
          fallback={<Empty>{props.symbolEmptyText()}</Empty>}
        >
          <List label="Symbols">
            <For each={props.symbolRows()}>
              {(item, i) => (
                <button
                  {...rowAttributes(i())}
                  class="tb-omnibar-row tb-symbol-row"
                  classList={{ selected: i() === props.selected() }}
                  onMouseDown={press(i(), () => props.onActivateSymbol(item.sym))}
                >
                  <span class="tb-symbol-kind" aria-hidden="true">
                    {kindIcon(item.sym.kind)}
                  </span>
                  <span class="tb-row-leaf">
                    {highlightSlice(item.sym.name, item.positions, 0)}
                  </span>
                  <Show when={props.symbolDir(item.sym).length > 0}>
                    <span class="tb-row-dir">{props.symbolDir(item.sym)}</span>
                  </Show>
                </button>
              )}
            </For>
          </List>
          <More />
        </Show>
      </Match>
      <Match when={props.mode() === "command"}>
        <Show when={props.commandRows().length > 0} fallback={<Empty>No matching commands</Empty>}>
          <List label="Commands">
            <For each={props.commandRows()}>
              {(item, i) => (
                <button
                  {...rowAttributes(i())}
                  class="tb-omnibar-row"
                  classList={{ selected: i() === props.selected() }}
                  onMouseDown={press(i(), () => props.onRunCommand(item.cmd))}
                >
                  <span class="tb-row-leaf">
                    {highlightSlice(item.cmd.title, item.positions, 0)}
                  </span>
                  <Show when={item.cmd.category}>
                    <span class="tb-row-dir">{item.cmd.category}</span>
                  </Show>
                  <Show when={item.cmd.keys.length > 0}>
                    <span class="tb-row-keys">{item.cmd.keys.map(formatKey).join(" / ")}</span>
                  </Show>
                </button>
              )}
            </For>
          </List>
        </Show>
      </Match>
      <Match when={props.mode() === "tree"}>
        <Show
          when={props.treeRows().length > 0}
          fallback={<Empty>{props.filesPending ? "Loading files…" : "No files"}</Empty>}
        >
          <List label="Files">
            <For each={props.treeRows()}>
              {(row, i) => (
                <button
                  {...rowAttributes(i())}
                  class="tb-omnibar-row tb-tree-row"
                  classList={{
                    dir: row.node.isDir,
                    selected: i() === props.selected(),
                    current: isCurrent(row.node.abs),
                  }}
                  style={`padding-left: ${10 + row.depth * 14}px`}
                  onMouseDown={press(i(), () =>
                    row.node.isDir
                      ? props.onToggleDir(row.node.key)
                      : props.onOpenFile(row.node.abs),
                  )}
                >
                  <span class="tb-tree-twisty" aria-hidden="true">
                    <Show when={row.node.isDir}>
                      <Show when={props.expanded().has(row.node.key)} fallback={<ChevronRight />}>
                        <ChevronDown />
                      </Show>
                    </Show>
                  </span>
                  <span class="tb-tree-icon" aria-hidden="true">
                    <Show when={row.node.isDir} fallback={<FileIcon />}>
                      <Show when={props.expanded().has(row.node.key)} fallback={<Folder />}>
                        <FolderOpen />
                      </Show>
                    </Show>
                  </span>
                  <span class="tb-row-leaf">{row.node.name}</span>
                </button>
              )}
            </For>
          </List>
        </Show>
      </Match>
      <Match when={props.mode() === "search"}>
        <Show
          when={props.fileRows().length > 0}
          fallback={<Empty>{props.filesPending ? "Loading files…" : "No matching files"}</Empty>}
        >
          <List label="Files">
            <For each={props.fileRows()}>
              {(item, i) => (
                <button
                  {...rowAttributes(i())}
                  class="tb-omnibar-row"
                  classList={{
                    selected: i() === props.selected(),
                    current: isCurrent(item.row.abs),
                  }}
                  onMouseDown={press(i(), () => props.onOpenFile(item.row.abs))}
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
          </List>
          <More />
        </Show>
      </Match>
    </Switch>
  );

  function PathRows(): JSX.Element {
    return (
      <Show when={props.pathReady()} fallback={<Empty>Loading…</Empty>}>
        <Show when={props.pathRows().length > 0} fallback={<Empty>No matching files</Empty>}>
          <List label="Files by path">
            <For each={props.pathRows()}>
              {(entry, i) => (
                <button
                  {...rowAttributes(i())}
                  class="tb-omnibar-row"
                  classList={{ selected: i() === props.selected(), dir: entry.isDir }}
                  onMouseDown={press(i(), () => props.onActivatePath(entry))}
                >
                  <span class="tb-tree-icon" aria-hidden="true">
                    <Show when={entry.isDir} fallback={<FileIcon />}>
                      <Folder />
                    </Show>
                  </span>
                  <span class="tb-row-leaf">{entry.name}</span>
                </button>
              )}
            </For>
          </List>
          <More />
        </Show>
      </Show>
    );
  }
}

// A glyph per symbol kind (see symbol-source's kindLabel), falling back to a generic mark.
function kindIcon(kind: string): JSX.Element {
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
}
