import { ChevronDown, ChevronRight, File, Files, Folder, FolderOpen } from "lucide-solid";
import { createEffect, createMemo, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { type PathTreeNode, pathAncestorKeys, visiblePathTreeRows } from "../../files/path-tree";
import { nextIndex } from "../../list-navigation";
import { samePath } from "../fs-path";
import type { ReviewFileView, ReviewOverview } from "./review-store";

interface DiffSize {
  added: number;
  removed: number;
}

export function ReviewFileTree(props: {
  expanded: () => ReadonlySet<string>;
  index: number;
  measure: (element: HTMLElement) => void;
  nodes: () => PathTreeNode<ReviewFileView>[];
  onSelect: (file: ReviewFileView) => void;
  onToggleDirectory: (key: string) => void;
  overview: () => ReviewOverview;
  selectedPath: () => string | null;
  style: string;
}): JSX.Element {
  const sizes = createMemo(() => {
    const result = new Map<string, DiffSize>();
    const visit = (node: PathTreeNode<ReviewFileView>): DiffSize => {
      if (node.kind === "file") {
        const summary = node.value.summary();
        const size = { added: summary.added, removed: summary.removed };
        result.set(node.key, size);
        return size;
      }
      const size = node.children.reduce(
        (total, child) => {
          const childSize = visit(child);
          total.added += childSize.added;
          total.removed += childSize.removed;
          return total;
        },
        { added: 0, removed: 0 },
      );
      result.set(node.key, size);
      return size;
    };
    for (const node of props.nodes()) {
      visit(node);
    }
    return result;
  });
  const visibleRows = createMemo(() =>
    visiblePathTreeRows(props.nodes(), props.expanded(), Number.POSITIVE_INFINITY),
  );
  const [focusedKey, setFocusedKey] = createSignal("");
  const rowButtons = new Map<string, HTMLButtonElement>();
  createEffect(() => {
    const rows = visibleRows();
    const current = focusedKey();
    if (rows.some((row) => row.node.key === current)) {
      return;
    }
    const selected = props.selectedPath();
    const preferred = rows.find(
      (row) =>
        row.node.kind === "file" &&
        selected !== null &&
        samePath(row.node.value.summary().path, selected),
    );
    setFocusedKey(preferred?.node.key ?? rows[0]?.node.key ?? "");
  });
  const focusRow = (key: string): void => {
    setFocusedKey(key);
    queueMicrotask(() => rowButtons.get(key)?.focus());
  };
  const activate = (node: PathTreeNode<ReviewFileView>): void => {
    if (node.kind === "directory") {
      props.onToggleDirectory(node.key);
    } else {
      props.onSelect(node.value);
    }
  };
  const onTreeKeyDown = (event: KeyboardEvent, node: PathTreeNode<ReviewFileView>): void => {
    const rows = visibleRows();
    const index = rows.findIndex((row) => row.node.key === node.key);
    let destination: string | undefined;
    switch (event.key) {
      case "ArrowDown":
        destination = rows[nextIndex(index, 1, rows.length, "clamp")]?.node.key;
        break;
      case "ArrowUp":
        destination = rows[nextIndex(index, -1, rows.length, "clamp")]?.node.key;
        break;
      case "Home":
        destination = rows[0]?.node.key;
        break;
      case "End":
        destination = rows.at(-1)?.node.key;
        break;
      case "ArrowRight":
        if (node.kind !== "directory") {
          return;
        }
        if (!props.expanded().has(node.key)) {
          props.onToggleDirectory(node.key);
        } else {
          destination = node.children[0]?.key;
        }
        break;
      case "ArrowLeft": {
        if (node.kind === "directory" && props.expanded().has(node.key)) {
          props.onToggleDirectory(node.key);
          break;
        }
        const ancestors = pathAncestorKeys(node.key);
        destination = ancestors[ancestors.length - 1];
        if (destination === undefined) {
          return;
        }
        break;
      }
      case "Enter":
      case " ":
        activate(node);
        break;
      default:
        return;
    }
    event.preventDefault();
    event.stopPropagation();
    if (destination !== undefined) {
      focusRow(destination);
    }
  };
  const TreeItems = (treeProps: { nodes: PathTreeNode<ReviewFileView>[] }): JSX.Element => (
    <For each={treeProps.nodes}>
      {(node) => {
        const directory = node.kind === "directory";
        const open = (): boolean => directory && props.expanded().has(node.key);
        const size = (): DiffSize => sizes().get(node.key) ?? { added: 0, removed: 0 };
        const active = (): boolean => {
          const selected = props.selectedPath();
          return (
            node.kind === "file" &&
            selected !== null &&
            samePath(node.value.summary().path, selected)
          );
        };
        onCleanup(() => rowButtons.delete(node.key));
        return (
          <div role="none">
            <button
              type="button"
              role="treeitem"
              class="unified-review-tree-row"
              classList={{ directory, file: !directory, active: active() }}
              title={
                directory
                  ? `${open() ? "Collapse" : "Expand"} ${node.name}`
                  : node.kind === "file"
                    ? node.value.summary().path
                    : ""
              }
              aria-expanded={directory ? open() : undefined}
              aria-selected={active()}
              tabIndex={focusedKey() === node.key ? 0 : -1}
              ref={(element) => rowButtons.set(node.key, element)}
              onClick={() => {
                setFocusedKey(node.key);
                activate(node);
              }}
              onFocus={() => setFocusedKey(node.key)}
              onKeyDown={(event) => onTreeKeyDown(event, node)}
            >
              <span class="unified-review-tree-twisty" aria-hidden="true">
                <Show when={directory}>
                  <Show when={open()} fallback={<ChevronRight />}>
                    <ChevronDown />
                  </Show>
                </Show>
              </span>
              <span class="unified-review-tree-icon" aria-hidden="true">
                <Show when={directory} fallback={<File />}>
                  <Show when={open()} fallback={<Folder />}>
                    <FolderOpen />
                  </Show>
                </Show>
              </span>
              <span class="unified-review-tree-name">{node.name}</span>
              <DiffSizeView added={size().added} removed={size().removed} class="" />
            </button>
            <Show when={node.kind === "directory" && open()}>
              <fieldset class="unified-review-tree-children">
                <TreeItems nodes={node.kind === "directory" ? node.children : []} />
              </fieldset>
            </Show>
          </div>
        );
      }}
    </For>
  );

  return (
    <section
      class="unified-review-files"
      data-index={props.index}
      ref={props.measure}
      style={props.style}
      aria-label="Changed files"
    >
      <header class="unified-review-files-header">
        <span class="unified-review-files-title">
          <Files size="1em" aria-hidden="true" />
          <strong>
            {props.overview().files.length} changed file
            {props.overview().files.length === 1 ? "" : "s"}
          </strong>
        </span>
        <DiffSizeView
          added={props.overview().added}
          removed={props.overview().removed}
          class="unified-review-totals"
        />
      </header>
      <div class="unified-review-tree" role="tree" aria-label="Changed files">
        <TreeItems nodes={props.nodes()} />
      </div>
    </section>
  );
}

function DiffSizeView(props: DiffSize & { class: string }): JSX.Element {
  return (
    <small class={`unified-review-file-stats ${props.class}`}>
      <span class="unified-review-added">+{props.added}</span>
      <span class="unified-review-removed">−{props.removed}</span>
    </small>
  );
}
