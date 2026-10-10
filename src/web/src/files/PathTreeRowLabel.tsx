import { ChevronDown, ChevronRight, File as FileIcon, Folder, FolderOpen } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { PathTreeNode } from "./path-tree";

/** A file-tree row's twisty, file/folder icon and name, shared by every tree-shaped file list. */
export function PathTreeRowLabel<T>(props: {
  node: PathTreeNode<T>;
  expanded: boolean;
  children: JSX.Element;
}): JSX.Element {
  return (
    <>
      <span class="tb-tree-twisty" aria-hidden="true">
        <Show when={props.node.kind === "directory"}>
          <Show when={props.expanded} fallback={<ChevronRight />}>
            <ChevronDown />
          </Show>
        </Show>
      </span>
      <span class="tb-tree-icon file-tree-icon" aria-hidden="true">
        <Show when={props.node.kind === "directory"} fallback={<FileIcon />}>
          <Show when={props.expanded} fallback={<Folder />}>
            <FolderOpen />
          </Show>
        </Show>
      </span>
      <span class="tb-row-leaf file-tree-name">{props.children}</span>
    </>
  );
}
