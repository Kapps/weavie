import { ChevronDown, ChevronRight, File, Folder, FolderOpen, X } from "lucide-solid";
import { createEffect, createSignal, For, type JSX, Match, onMount, Show, Switch } from "solid-js";
import { normalizePath, samePath } from "../editor/fs-path";

// One directory entry the host returned: leaf name, absolute path, and whether it's a folder.
export interface DirEntry {
  name: string;
  path: string;
  isDir: boolean;
}

export type DirectoryState =
  | { status: "loading" }
  | { status: "ready"; entries: DirEntry[] }
  | { status: "error"; message: string };

// Directory state keyed by absolute path, filled lazily as folders are expanded.
export type DirListings = Record<string, DirectoryState>;

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
  createEffect(() => {
    if (props.entry.isDir && open() && props.listings[props.entry.path] === undefined) {
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
          <Directory
            path={props.entry.path}
            emptyLabel="Empty folder"
            listings={props.listings}
            currentFile={props.currentFile}
            onExpand={props.onExpand}
            onOpen={props.onOpen}
          />
        </div>
      </Show>
    </div>
  );
}

function Directory(props: {
  path: string;
  emptyLabel: string;
  listings: DirListings;
  currentFile: string | null;
  onExpand: (path: string) => void;
  onOpen: (path: string) => void;
}): JSX.Element {
  const state = (): DirectoryState | undefined => props.listings[props.path];
  const error = (): Extract<DirectoryState, { status: "error" }> | undefined => {
    const current = state();
    return current?.status === "error" ? current : undefined;
  };
  const ready = (): Extract<DirectoryState, { status: "ready" }> | undefined => {
    const current = state();
    return current?.status === "ready" ? current : undefined;
  };

  return (
    <Switch>
      <Match when={error()} keyed>
        {(failure) => (
          <div class="browser-error" role="alert">
            <span>{failure.message}</span>
            <button type="button" onClick={() => props.onExpand(props.path)}>
              Retry
            </button>
          </div>
        )}
      </Match>
      <Match when={ready()} keyed>
        {(listing) => (
          <Show
            when={listing.entries.length > 0}
            fallback={<div class="browser-empty">{props.emptyLabel}</div>}
          >
            <For each={listing.entries}>
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
          </Show>
        )}
      </Match>
      <Match when={true}>
        <div class="browser-loading" role="status">
          Loading…
        </div>
      </Match>
    </Switch>
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
    <div class="browser-panel" role="group" onKeyDown={onKeyDown}>
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
        <Directory
          path={props.root}
          emptyLabel="No files"
          listings={props.listings}
          currentFile={props.currentFile}
          onExpand={props.onExpand}
          onOpen={props.onOpen}
        />
      </div>
    </div>
  );
}
