import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import { normalizePath } from "../editor/fs-path";
import { createSessionOwnedMap, createSessionOwnedState } from "../messaging/session-owned-state";
import type { DirEntry, DirListings } from "./FileBrowser";
import { revealFileIn } from "./reveal";

interface FileIndex {
  root: string | null;
  /** The host's home directory, so `~/…` in open-by-path expands against the host, not the browser. */
  home: string | null;
  files: string[];
  pending: boolean;
}

// The index and the directory listings are separate signals on purpose. Held together, every listing reply
// invalidated readers of the index too — so a consumer deriving a request from the index (open-by-path) would
// re-request on its own reply and never settle. Split, that whole class of loop can't be written.
const EMPTY_INDEX: FileIndex = { root: null, home: null, files: [], pending: false };
const indexes = createSessionOwnedState<FileIndex>(() => EMPTY_INDEX);
const listings = createSessionOwnedState<DirListings>(() => ({}));
const requests = createSessionOwnedMap<string, { again: boolean }>();

function updateListings(
  session: ClientSession,
  mutate: (current: DirListings) => DirListings,
): void {
  listings.update(session, mutate);
}

export const selectedFileIndex = (): FileIndex => {
  return indexes.get(selectedSession()) ?? EMPTY_INDEX;
};
export const selectedDirectoryListings = (): DirListings => {
  return listings.get(selectedSession()) ?? {};
};

/** As {@link revealFileIn}, for whichever session is selected. */
export function revealSelectedFile(path: string, line: number | undefined, preview = false): void {
  revealFileIn(selectedSession(), path, line, preview);
}

export function refreshSelectedFileIndex(): void {
  selectedSession()?.feature("files").publish("refreshIndex", {});
}

export function listSelectedDirectory(path: string): void {
  const session = selectedSession();
  if (session !== null) listDirectory(session, path);
}

/** Drops a directory's cached listing once the browser collapses it, and tells the host to stop watching it. */
export function unlistSelectedDirectory(path: string): void {
  const session = selectedSession();
  if (session === null) return;
  requests.delete(session, path);
  updateListings(session, (current) => {
    if (!(path in current)) return current;
    const { [path]: _removed, ...rest } = current;
    return rest;
  });
  session.feature("files").publish("unwatchDirectory", { path });
}

function listDirectory(session: ClientSession, path: string): void {
  const pending = requests.get(session, path);
  if (pending !== undefined) {
    pending.again = true;
    return;
  }
  const request = { again: false };
  requests.set(session, path, request);
  if (listings.get(session)?.[path]?.status !== "ready") {
    updateListings(session, (current) => ({ ...current, [path]: { status: "loading" } }));
  }
  void (async () => {
    do {
      request.again = false;
      try {
        const { entries } = await session
          .feature("files")
          .request<{ entries: DirEntry[] }, { path: string }>("listDirectory", { path });
        if (request.again || session.closed) continue;
        updateListings(session, (current) => {
          const previous = current[path];
          const known = new Map(
            previous?.status === "ready"
              ? previous.entries.map((entry) => [entry.path, entry])
              : [],
          );
          return {
            ...current,
            [path]: {
              status: "ready",
              entries: entries.map((entry) => {
                const old = known.get(entry.path);
                return old?.name === entry.name && old.isDir === entry.isDir ? old : entry;
              }),
            },
          };
        });
      } catch (error: unknown) {
        if (!request.again)
          updateListings(session, (current) => ({
            ...current,
            [path]: {
              status: "error",
              message: error instanceof Error ? error.message : String(error),
            },
          }));
      }
    } while (request.again && !session.closed);
    requests.delete(session, path);
  })();
}

registerSessionFeature((session) => {
  const files = session.feature("files");
  const offChanged = files.on<{ changes: { path: string }[] }>("changed", ({ changes }) => {
    const changed = changes.map((change) => normalizePath(change.path));
    for (const path of Object.keys(listings.get(session) ?? {})) {
      const directory = normalizePath(path);
      if (
        changed.some(
          (change) =>
            change === directory ||
            change.startsWith(`${directory}/`) ||
            directory.startsWith(`${change}/`),
        )
      ) {
        listDirectory(session, path);
      }
    }
  });
  const offIndex = files.on<{ root: string; home?: string; files: string[]; pending?: boolean }>(
    "index",
    (message) => {
      const previousRoot = indexes.get(session)?.root ?? null;
      if (message.pending === true && message.root === previousRoot) {
        for (const path of Object.keys(listings.get(session) ?? {})) listDirectory(session, path);
        return;
      }
      indexes.update(session, () => ({
        root: message.root,
        // An empty profile path is no answer: `~` stays unresolvable rather than naming the root.
        home: message.home === undefined || message.home === "" ? null : message.home,
        files: message.files,
        pending: message.pending === true,
      }));
      if (message.root !== previousRoot) {
        updateListings(session, () => ({}));
      }
    },
  );
  return () => {
    offIndex();
    offChanged();
  };
});
