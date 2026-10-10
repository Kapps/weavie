import { createMemo, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { createFileFinder, type FileRow, rankFiles, splitPath } from "../../chrome/file-search";
import { registerFloatingPanel } from "../../chrome/floating-panels";
import { highlightSlice } from "../../chrome/highlight";
import { dismissOnOutsideInteraction } from "../../chrome/popover-dismiss";
import { PathTreeRowLabel } from "../../files/PathTreeRowLabel";
import { buildPathTree, compactPathTree } from "../../files/path-tree";
import { createListNavigation } from "../../list-navigation";
import { DiffStats } from "./DiffStats";
import { ReviewedMark } from "./ReviewedMark";
import type { ReviewFileView } from "./review-store";
import { createFoldableTree, isReviewed } from "./review-tree";

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
  // A filter reopens every folder so no match hides in a collapsed one.
  const { expanded, rows, activate, fold, expandAll } = createFoldableTree(tree, (entry) =>
    props.onChoose(entry.index),
  );
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
      if (node !== undefined) activate(node);
    },
    onDismiss: () => props.onCancel(),
  });
  const onKeyDown = (event: KeyboardEvent): void => {
    if (nav.onKeyDown(event)) return;
    const node = rows()[nav.index()]?.node;
    if (node !== undefined && fold(node, event.key)) event.preventDefault();
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
            expandAll();
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
                const entry = () => (row.node.kind === "file" ? row.node.value : undefined);
                return (
                  <button
                    {...nav.row(index())}
                    type="button"
                    role="treeitem"
                    class="tb-omnibar-row tb-tree-row file-tree-row"
                    classList={{
                      dir: row.node.kind === "directory",
                      selected: index() === nav.index(),
                      current: entry() !== undefined && entry()!.index === props.current(),
                      reviewed: entry() !== undefined && isReviewed(entry()!.file),
                    }}
                    style={`padding-left:${10 + row.depth * 14}px`}
                    aria-selected={index() === nav.index()}
                    title={row.node.key}
                    // mousedown keeps the focus (and the keyboard) in the filter box.
                    onMouseDown={(event) => {
                      event.preventDefault();
                      activate(row.node);
                    }}
                  >
                    <PathTreeRowLabel node={row.node} expanded={expanded().has(row.node.key)}>
                      {highlightSlice(
                        row.node.name,
                        entry()?.positions,
                        row.node.key.length - row.node.name.length,
                      )}
                    </PathTreeRowLabel>
                    <Show when={entry()}>
                      {(file) => (
                        <>
                          <DiffStats
                            added={file().file.summary().added}
                            removed={file().file.summary().removed}
                          />
                          <ReviewedMark reviewed={isReviewed(file().file)} />
                        </>
                      )}
                    </Show>
                  </button>
                );
              }}
            </For>
          </Show>
        </div>
        <footer>
          {all().filter((entry) => isReviewed(entry.file)).length} of {props.files().length}{" "}
          reviewed
        </footer>
      </div>
    </Portal>
  );
}
