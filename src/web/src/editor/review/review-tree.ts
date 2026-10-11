import { type Accessor, createMemo, createSignal } from "solid-js";
import {
  type PathTreeNode,
  type PathTreeRow,
  pathTreeDirectoryKeys,
  visiblePathTreeRows,
} from "../../files/path-tree";
import { type ReviewFileView, reviewProgress } from "./review-store";

export interface FoldableTree<T> {
  expanded: Accessor<ReadonlySet<string>>;
  rows: Accessor<PathTreeRow<T>[]>;
  /** Folds a folder, or chooses a file. */
  activate: (node: PathTreeNode<T>) => void;
  /** Right opens a closed folder, left closes an open one; true when the key folded something. */
  fold: (node: PathTreeNode<T>, key: string) => boolean;
  expandAll: () => void;
}

/** A path tree whose folders start open and fold individually. */
export function createFoldableTree<T>(
  tree: Accessor<PathTreeNode<T>[]>,
  choose: (value: T) => void,
): FoldableTree<T> {
  const [collapsed, setCollapsed] = createSignal<ReadonlySet<string>>(new Set());
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
  const fold = (node: PathTreeNode<T>, key: string): boolean => {
    if (node.kind !== "directory") return false;
    const open = expanded().has(node.key);
    if (key !== (open ? "ArrowLeft" : "ArrowRight")) return false;
    toggle(node.key);
    return true;
  };
  return {
    expanded,
    rows,
    activate: (node) => (node.kind === "directory" ? toggle(node.key) : choose(node.value)),
    fold,
    expandAll: () => void setCollapsed(new Set<string>()),
  };
}

export const isReviewed = (file: ReviewFileView): boolean => reviewProgress(file).fraction === 1;
