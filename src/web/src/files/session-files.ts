import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import type { DirEntry, DirListings } from "./FileBrowser";

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
const [indexes, setIndexes] = createSignal<Map<ClientSession, FileIndex>>(new Map());
const [listings, setListings] = createSignal<Map<ClientSession, DirListings>>(new Map());

function updateListings(
  session: ClientSession,
  mutate: (current: DirListings) => DirListings,
): void {
  setListings((previous) => {
    const current = previous.get(session);
    if (current === undefined) {
      return previous;
    }
    const next = new Map(previous);
    next.set(session, mutate(current));
    return next;
  });
}

export const selectedFileIndex = (): FileIndex => {
  const session = selectedSession();
  return session === null ? EMPTY_INDEX : (indexes().get(session) ?? EMPTY_INDEX);
};
export const selectedDirectoryListings = (): DirListings => {
  const session = selectedSession();
  return session === null ? {} : (listings().get(session) ?? {});
};

export function revealSelectedFile(path: string, line: number, preview = false): void {
  selectedSession()?.feature("files").publish("reveal", { path, line, preview });
}

export function refreshSelectedFileIndex(): void {
  selectedSession()?.feature("files").publish("refreshIndex", {});
}

export function listSelectedDirectory(path: string): void {
  const session = selectedSession();
  if (session === null) {
    return;
  }
  if (listings().get(session)?.[path]?.status === "loading") {
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
  setIndexes((previous) => new Map(previous).set(session, EMPTY_INDEX));
  setListings((previous) => new Map(previous).set(session, {}));
  const files = session.feature("files");
  const offIndex = files.on<{ root: string; home?: string; files: string[]; pending?: boolean }>(
    "index",
    (message) => {
      const previousRoot = indexes().get(session)?.root ?? null;
      if (message.pending === true && message.root === previousRoot) {
        return;
      }
      setIndexes((previous) =>
        previous.has(session)
          ? new Map(previous).set(session, {
              root: message.root,
              // An empty profile path is no answer: `~` stays unresolvable rather than naming the root.
              home: message.home === undefined || message.home === "" ? null : message.home,
              files: message.files,
              pending: message.pending === true,
            })
          : previous,
      );
      if (message.root !== previousRoot) {
        updateListings(session, () => ({}));
      }
    },
  );
  return () => {
    offIndex();
    setIndexes((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
    setListings((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});
