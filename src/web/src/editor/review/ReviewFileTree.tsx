import { createMemo, createSignal, Index, type JSX } from "solid-js";
import { PathTreeRowLabel } from "../../files/PathTreeRowLabel";
import {
  buildPathTree,
  compactPathTree,
  type PathTreeNode,
  pathAncestorKeys,
} from "../../files/path-tree";
import { nextIndex } from "../../list-navigation";
import { DiffStats } from "./DiffStats";
import { ReviewedMark } from "./ReviewedMark";
import type { ReviewFileView } from "./review-store";
import { createFoldableTree, isReviewed } from "./review-tree";

interface Entry {
  index: number;
  file: ReviewFileView;
}

/** The review's changed files as a folder tree above the diffs; choosing a file jumps the review to it. */
export function ReviewFileTree(props: {
  files: () => ReviewFileView[];
  current: () => number | undefined;
  displayPath: (path: string) => string;
  onChoose: (index: number) => void;
  ref: (element: HTMLElement) => void;
}): JSX.Element {
  const tree = createMemo(() =>
    compactPathTree(
      buildPathTree(
        props.files().map((file, index) => ({
          path: props.displayPath(file.summary().path),
          value: { index, file } satisfies Entry,
        })),
      ),
    ),
  );
  const { expanded, rows, activate, fold } = createFoldableTree(tree, (entry) =>
    props.onChoose(entry.index),
  );
  const totals = (node: PathTreeNode<Entry>): { added: number; removed: number } => {
    if (node.kind === "file") return node.value.file.summary();
    return node.children
      .map(totals)
      .reduce(
        (sum, child) => ({ added: sum.added + child.added, removed: sum.removed + child.removed }),
        { added: 0, removed: 0 },
      );
  };

  // One row takes the tab stop; the arrows move it, as in any tree.
  const [focused, setFocused] = createSignal(0);
  const buttons: HTMLButtonElement[] = [];
  const focusRow = (index: number): void => {
    setFocused(index);
    buttons[index]?.focus();
  };
  const onKeyDown = (event: KeyboardEvent, index: number): void => {
    const node = rows()[index]!.node;
    const count = rows().length;
    const moves: Record<string, () => number> = {
      ArrowDown: () => nextIndex(index, 1, count, "clamp"),
      ArrowUp: () => nextIndex(index, -1, count, "clamp"),
      Home: () => 0,
      End: () => count - 1,
      ArrowLeft: () =>
        rows().findIndex((row) => row.node.key === pathAncestorKeys(node.key).at(-1)),
    };
    if (event.key === "Enter" || event.key === " ") activate(node);
    else if (!fold(node, event.key)) {
      const to = moves[event.key]?.() ?? -1;
      if (to < 0) return;
      focusRow(to);
    }
    event.preventDefault();
    event.stopPropagation();
  };

  return (
    <div class="unified-review-tree" role="tree" aria-label="Changed files" ref={props.ref}>
      <Index each={rows()}>
        {(row, index) => {
          const node = () => row().node;
          const entry = () => {
            const value = node();
            return value.kind === "file" ? value.value : undefined;
          };
          const current = () => entry() !== undefined && entry()!.index === props.current();
          const reviewed = () => entry() !== undefined && isReviewed(entry()!.file);
          const directory = () => node().kind === "directory";
          return (
            <button
              type="button"
              role="treeitem"
              class="unified-review-tree-row file-tree-row"
              classList={{ dir: directory(), current: current(), reviewed: reviewed() }}
              style={`--depth:${row().depth}`}
              title={node().key}
              aria-expanded={directory() ? expanded().has(node().key) : undefined}
              aria-selected={current()}
              aria-level={row().depth + 1}
              tabIndex={Math.min(focused(), rows().length - 1) === index ? 0 : -1}
              ref={(element) => {
                buttons[index] = element;
              }}
              onFocus={() => setFocused(index)}
              onClick={() => activate(node())}
              onKeyDown={(event) => onKeyDown(event, index)}
            >
              <PathTreeRowLabel node={node()} expanded={expanded().has(node().key)}>
                {node().name}
              </PathTreeRowLabel>
              <DiffStats added={totals(node()).added} removed={totals(node()).removed} />
              <ReviewedMark reviewed={reviewed()} />
            </button>
          );
        }}
      </Index>
    </div>
  );
}
