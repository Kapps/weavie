import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import { createSessionOwnedState } from "../messaging/session-owned-state";
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
  if (session === null) {
    return;
  }
  if (listings.get(session)?.[path]?.status === "loading") {
    return;
  }
  updateListings(session, (current) => ({ ...current, [path]: { status: "loading" } }));
  void session
    .feature("files")
    .request<{ entries: DirEntry[] }, { path: string }>("listDirectory", { path })
    .then(({ entries }) => {
      updateListings(session, (current) => ({
        ...current,
        [path]: { status: "ready", entries },
      }));
    })
    .catch((error: unknown) => {
      updateListings(session, (current) => ({
        ...current,
        [path]: {
          status: "error",
          message: error instanceof Error ? error.message : String(error),
        },
      }));
    });
}

registerSessionFeature((session) => {
  const files = session.feature("files");
  const offIndex = files.on<{ root: string; home?: string; files: string[]; pending?: boolean }>(
    "index",
    (message) => {
      const previousRoot = indexes.get(session)?.root ?? null;
      if (message.pending === true && message.root === previousRoot) {
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
  return offIndex;
});
