import { ChevronDown, ChevronRight, File, Folder, FolderOpen, X } from "lucide-solid";
import { For, type JSX, Show, createEffect, createSignal, onMount } from "solid-js";
import { normalizePath, samePath } from "../editor/fs-path";

// One directory entry the host returned: leaf name, absolute path, and whether it's a folder.
export interface DirEntry {
  name: string;
  path: string;
  isDir: boolean;
}

// Directory listings keyed by absolute directory path, filled in lazily as folders are expanded.
export type DirListings = Record<string, DirEntry[]>;

// Compare by normalized identity (see fs-path.ts): the host lists native paths while currentFile arrives in
// uriHostPath spelling, so a separator/case-sensitive match would never hit on Windows.
function isAncestorPath(dir: string, file: string | null): boolean {
  return file !== null && normalizePath(file).startsWith(`${normalizePath(dir)}/`);
}

function leafName(path: string): string {
  const parts = path.split(/[\\/]/).filter((p) => p.length > 0);
  return parts.length > 0 ? parts[parts.length - 1]! : path;
}

// A single tree row + (when open) its children. Folders toggle and lazily request their listing on first
// open; files open in the editor on click. An ancestor of the current file starts open ("reveal" behavior).
function Node(props: {
  entry: DirEntry;
  listings: DirListings;
  currentFile: string | null;
  onExpand: (path: string) => void;
  onOpen: (path: string) => void;
}): JSX.Element {
  const [open, setOpen] = createSignal(
    props.entry.isDir && isAncestorPath(props.entry.path, props.currentFile),
  );
  const children = (): DirEntry[] | undefined => props.listings[props.entry.path];

  // When this folder is open and its listing hasn't loaded, ask the host for it. Covers a manual toggle
  // and an ancestor's auto-open; once loaded the condition is false, so it fires at most once.
  createEffect(() => {
    if (props.entry.isDir && open() && children() === undefined) {
      props.onExpand(props.entry.path);
    }
  });

  const onClick = (): void => {
    if (props.entry.isDir) {
      setOpen((v) => !v);
    } else {
      props.onOpen(props.entry.path);
    }
  };

  return (
    <div class="browser-node">
      <button
        type="button"
        classList={{
          "browser-row": true,
          active: props.currentFile !== null && samePath(props.currentFile, props.entry.path),
        }}
        title={props.entry.path}
        onClick={onClick}
      >
        <span class="browser-twisty">
          <Show when={props.entry.isDir}>
            <Show when={open()} fallback={<ChevronRight />}>
              <ChevronDown />
            </Show>
          </Show>
        </span>
        <span class="browser-icon">
          <Show when={props.entry.isDir} fallback={<File />}>
            <Show when={open()} fallback={<Folder />}>
              <FolderOpen />
            </Show>
          </Show>
        </span>
        <span class="browser-name">{props.entry.name}</span>
      </button>
      <Show when={props.entry.isDir && open()}>
        <div class="browser-children">
          <For each={children()} fallback={<div class="browser-loading">…</div>}>
            {(child) => (
              <Node
                entry={child}
                listings={props.listings}
                currentFile={props.currentFile}
                onExpand={props.onExpand}
                onOpen={props.onOpen}
              />
            )}
          </For>
        </div>
      </Show>
    </div>
  );
}

// The contextual file browser: a fixed overlay (not a layout pane) rooted at the session's workspace
// directory, sitting above the editor and pane tree. Folders expand lazily; clicking a file opens it.
export default function FileBrowser(props: {
  root: string;
  listings: DirListings;
  currentFile: string | null;
  onExpand: (path: string) => void;
  onOpen: (path: string) => void;
  onClose: () => void;
}): JSX.Element {
  let closeButton: HTMLButtonElement | undefined;
  // Escape closes the panel (matching the omnibar/dialogs). Scoped to the panel — focus is moved into it on
  // open — so it never hijacks the editor's own Escape (suggestions, etc.).
  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "Escape") {
      e.preventDefault();
      props.onClose();
    }
  };
  onMount(() => closeButton?.focus());
  return (
    <div class="browser-panel" onKeyDown={onKeyDown}>
      <div class="browser-head">
        <span class="browser-title" title={props.root}>
          {leafName(props.root)}
        </span>
        <button
          type="button"
          class="browser-close"
          title="Close (Esc)"
          ref={closeButton}
          onClick={() => props.onClose()}
        >
          <X />
        </button>
      </div>
      <div class="browser-body">
        <For
          each={props.listings[props.root]}
          fallback={<div class="browser-loading">Loading…</div>}
        >
          {(entry) => (
            <Node
              entry={entry}
              listings={props.listings}
              currentFile={props.currentFile}
              onExpand={props.onExpand}
              onOpen={props.onOpen}
            />
          )}
        </For>
      </div>
    </div>
  );
}
