import { ChevronDown, ChevronRight, File, Folder, FolderOpen } from "lucide-solid";
import {
  createMemo,
  createSignal,
  For,
  type JSX,
  Match,
  onCleanup,
  Show,
  Switch,
  untrack,
} from "solid-js";
import type { ClientSession } from "../bridge";
import { normalizePath, samePath } from "../editor/fs-path";
import { BrowserFilter } from "./BrowserFilter";
import { acquireDirectory } from "./session-files";

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

// A single tree row + (when open) its children. Folders toggle and lazily request their listing on first
// open; files open in the editor on click. An ancestor of the current file starts open ("reveal" behavior).
function Node(props: {
  entry: DirEntry;
  listings: DirListings;
  currentFile: string | null;
  session: ClientSession;
  onOpen: (path: string) => void;
}): JSX.Element {
  const [open, setOpen] = createSignal(
    props.entry.isDir && isAncestorPath(props.entry.path, props.currentFile),
  );
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
          "file-tree-row": true,
          dir: props.entry.isDir,
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
        <span class="browser-icon file-tree-icon">
          <Show when={props.entry.isDir} fallback={<File />}>
            <Show when={open()} fallback={<Folder />}>
              <FolderOpen />
            </Show>
          </Show>
        </span>
        <span class="browser-name file-tree-name">{props.entry.name}</span>
      </button>
      <Show when={props.entry.isDir && open()}>
        <div class="browser-children">
          <Directory
            path={props.entry.path}
            emptyLabel="Empty folder"
            listings={props.listings}
            currentFile={props.currentFile}
            session={props.session}
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
  session: ClientSession;
  onOpen: (path: string) => void;
}): JSX.Element {
  const lease = untrack(() => acquireDirectory(props.session, props.path));
  onCleanup(lease.release);
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
            <button type="button" onClick={lease.refresh}>
              Retry
            </button>
          </div>
        )}
      </Match>
      <Match when={ready()}>
        {(listing) => (
          <Show
            when={listing().entries.length > 0}
            fallback={<div class="browser-empty">{props.emptyLabel}</div>}
          >
            <For each={listing().entries}>
              {(entry) => (
                <Node
                  entry={entry}
                  listings={props.listings}
                  currentFile={props.currentFile}
                  session={props.session}
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

// The contextual file browser content, rooted at the session's workspace
// directory, sitting above the editor and pane tree. Folders expand lazily; clicking a file opens it.
export default function FileBrowser(props: {
  root: string;
  session: ClientSession | null;
  files: string[];
  pending: boolean;
  filterRequest: { session: ClientSession } | null;
  onFilterRequestHandled: () => void;
  listings: DirListings;
  currentFile: string | null;
  onOpen: (path: string) => void;
}): JSX.Element {
  const owner = createMemo(
    () => {
      const session = props.session;
      return session === null ? null : { session, path: props.root };
    },
    null,
    {
      equals: (previous, next) =>
        previous?.session === next?.session && previous?.path === next?.path,
    },
  );
  return (
    <div class="browser-panel" role="group">
      <BrowserFilter
        root={props.root}
        session={props.session}
        files={props.files}
        pending={props.pending}
        request={props.filterRequest}
        onRequestHandled={props.onFilterRequestHandled}
        currentFile={props.currentFile}
        onOpen={props.onOpen}
      >
        <Show when={owner()} keyed>
          {(directory) => (
            <Directory
              path={directory.path}
              session={directory.session}
              emptyLabel="No files"
              listings={props.listings}
              currentFile={props.currentFile}
              onOpen={props.onOpen}
            />
          )}
        </Show>
      </BrowserFilter>
    </div>
  );
}
