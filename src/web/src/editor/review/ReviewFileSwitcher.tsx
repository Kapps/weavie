import { createMemo, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { createFileFinder, type FileRow, rankFiles, splitPath } from "../../chrome/file-search";
import { registerFloatingPanel } from "../../chrome/floating-panels";
import { highlightSlice } from "../../chrome/highlight";
import { dismissOnOutsideInteraction } from "../../chrome/popover-dismiss";
import { PathTreeRowLabel } from "../../files/PathTreeRowLabel";
import {
  buildPathTree,
  compactPathTree,
  type PathTreeNode,
  pathTreeDirectoryKeys,
  visiblePathTreeRows,
} from "../../files/path-tree";
import { createListNavigation } from "../../list-navigation";
import { DiffStats } from "./DiffStats";
import { type ReviewFileView, reviewProgress } from "./review-store";

interface Entry {
  index: number;
  file: ReviewFileView;
  row: FileRow;
  positions: Set<number> | undefined;
}

/** The review's files as a filterable folder tree, opened under `anchor`; choosing one jumps the review to it. */
export function ReviewFileSwitcher(props: {
  anchor: HTMLElement;
  files: () => ReviewFileView[];
  current: () => number | undefined;
  displayPath: (path: string) => string;
  onChoose: (index: number) => void;
  /** Dismissed by clicking elsewhere, which keeps the focus where the user put it. */
  onClose: () => void;
  /** Dismissed from the keyboard, which returns focus to the review. */
  onCancel: () => void;
}): JSX.Element {
  let input: HTMLInputElement | undefined;
  const [query, setQuery] = createSignal("");
  const all = createMemo(() =>
    props.files().map(
      (file, index): Entry => ({
        index,
        file,
        row: splitPath(props.displayPath(file.summary().path), ""),
        positions: undefined,
      }),
    ),
  );
  const finder = createMemo(() => createFileFinder(all().map((entry) => entry.row)));
  const matches = createMemo(() => {
    const q = query().trim();
    if (q.length === 0) return all();
    const byPath = new Map(all().map((entry) => [entry.row.abs, entry]));
    return rankFiles(finder(), q, [], null).matches.map(
      (match): Entry => ({ ...byPath.get(match.row.abs)!, positions: match.positions }),
    );
  });
  const tree = createMemo(() =>
    compactPathTree(
      buildPathTree(matches().map((entry) => ({ path: entry.row.rel, value: entry }))),
    ),
  );
  // Every folder starts open; a filter reopens them all so no match hides in a collapsed folder.
  const [collapsed, setCollapsed] = createSignal(new Set<string>());
  const expanded = createMemo(
    () => new Set(pathTreeDirectoryKeys(tree()).filter((key) => !collapsed().has(key))),
  );
  const rows = createMemo(() => visiblePathTreeRows(tree(), expanded(), Number.POSITIVE_INFINITY));
  const toggle = (key: string): void => {
    setCollapsed((keys) => {
      const next = new Set(keys);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  };

  const isReviewed = (entry: Entry): boolean => reviewProgress(entry.file).fraction === 1;
  const filesUnder = (node: PathTreeNode<Entry>): Entry[] =>
    node.kind === "file" ? [node.value] : node.children.flatMap(filesUnder);
  const firstFileRow = (): number => rows().findIndex((row) => row.node.kind === "file");

  const nav = createListNavigation({
    count: () => rows().length,
    edges: "clamp",
    initialIndex: Math.max(
      0,
      rows().findIndex(
        (row) => row.node.kind === "file" && row.node.value.index === props.current(),
      ),
    ),
    acceptKeys: ["Enter"],
    onAccept: (index) => {
      const node = rows()[index]?.node;
      if (node?.kind === "directory") toggle(node.key);
      else if (node !== undefined) props.onChoose(node.value.index);
    },
    onDismiss: () => props.onCancel(),
  });
  const onKeyDown = (event: KeyboardEvent): void => {
    if (nav.onKeyDown(event)) return;
    const node = rows()[nav.index()]?.node;
    if (node?.kind !== "directory") return;
    const open = expanded().has(node.key);
    if ((event.key === "ArrowLeft" && open) || (event.key === "ArrowRight" && !open)) {
      event.preventDefault();
      toggle(node.key);
    }
  };

  // Opens right under the file name it was invoked from, kept inside the window.
  const anchor = props.anchor.getBoundingClientRect();
  const top = anchor.bottom + 4;
  const left = Math.max(8, Math.min(anchor.left, window.innerWidth - 448));

  onMount(() => {
    input?.focus();
    nav.reveal("nearest");
    // The filename buttons toggle the list themselves.
    dismissOnOutsideInteraction(
      ".unified-review-switcher, .unified-review-file-name",
      props.onClose,
    );
    onCleanup(registerFloatingPanel("review-file-switcher", props.onCancel, "popover").dispose);
  });

  return (
    <Portal>
      <div
        class="unified-review-switcher"
        style={`top:${top}px;left:${left}px;max-height:${window.innerHeight - top - 12}px`}
      >
        <input
          ref={input}
          type="text"
          placeholder={`Filter ${props.files().length} changed files…`}
          value={query()}
          spellcheck={false}
          autocomplete="off"
          onInput={(event) => {
            setQuery(event.currentTarget.value);
            setCollapsed(new Set<string>());
            nav.setIndex(Math.max(0, firstFileRow()));
          }}
          onKeyDown={onKeyDown}
        />
        <div class="tb-omnibar-list" role="tree">
          <Show
            when={rows().length > 0}
            fallback={<div class="unified-review-switcher-empty">No matching files.</div>}
          >
            <For each={rows()}>
              {(row, index) => {
                const files = () => filesUnder(row.node);
                return (
                  <button
                    {...nav.row(index())}
                    type="button"
                    role="treeitem"
                    class="tb-omnibar-row tb-tree-row file-tree-row"
                    classList={{
                      dir: row.node.kind === "directory",
                      selected: index() === nav.index(),
                      current: row.node.kind === "file" && row.node.value.index === props.current(),
                    }}
                    style={`padding-left:${10 + row.depth * 14}px`}
                    aria-selected={index() === nav.index()}
                    title={row.node.key}
                    // mousedown keeps the focus (and the keyboard) in the filter box.
                    onMouseDown={(event) => {
                      event.preventDefault();
                      const node = row.node;
                      if (node.kind === "directory") toggle(node.key);
                      else props.onChoose(node.value.index);
                    }}
                  >
                    <PathTreeRowLabel node={row.node} expanded={expanded().has(row.node.key)}>
                      {highlightSlice(
                        row.node.name,
                        files()[0]?.positions,
                        row.node.key.length - row.node.name.length,
                      )}
                    </PathTreeRowLabel>
                    <span class="unified-review-switcher-meta">
                      <Show
                        when={row.node.kind === "file" ? files()[0] : undefined}
                        fallback={
                          <>
                            {files().length}
                            {files().some(isReviewed)
                              ? ` · ✓ ${files().filter(isReviewed).length}`
                              : ""}
                          </>
                        }
                      >
                        {(entry) => (
                          <>
                            <DiffStats
                              added={entry().file.summary().added}
                              removed={entry().file.summary().removed}
                            />
                            <span
                              class="unified-review-switcher-state"
                              classList={{ reviewed: isReviewed(entry()) }}
                            />
                          </>
                        )}
                      </Show>
                    </span>
                  </button>
                );
              }}
            </For>
          </Show>
        </div>
        <footer>
          <span>↑↓ move · ←→ fold · ↵ jump · Esc close</span>
          <span>
            {all().filter(isReviewed).length} of {props.files().length} reviewed
          </span>
        </footer>
      </div>
    </Portal>
  );
}
