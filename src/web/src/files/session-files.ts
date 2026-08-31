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

interface SessionFiles {
  index: FileIndex;
  listings: DirListings;
}

const EMPTY: SessionFiles = {
  index: { root: null, home: null, files: [], pending: false },
  listings: {},
};
const [states, setStates] = createSignal<Map<ClientSession, SessionFiles>>(new Map());

function update(session: ClientSession, mutate: (current: SessionFiles) => SessionFiles): void {
  setStates((previous) => {
    const current = previous.get(session);
    if (current === undefined) {
      return previous;
    }
    const next = new Map(previous);
    next.set(session, mutate(current));
    return next;
  });
}

function selectedState(): SessionFiles {
  const session = selectedSession();
  return session === null ? EMPTY : (states().get(session) ?? EMPTY);
}

export const selectedFileIndex = (): FileIndex => selectedState().index;
export const selectedDirectoryListings = (): DirListings => selectedState().listings;

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
  if (states().get(session)?.listings[path]?.status === "loading") {
    return;
  }
  update(session, (current) => ({
    ...current,
    listings: { ...current.listings, [path]: { status: "loading" } },
  }));
  void session
    .feature("files")
    .request<{ entries: DirEntry[] }, { path: string }>("listDirectory", { path })
    .then(({ entries }) => {
      update(session, (current) => ({
        ...current,
        listings: { ...current.listings, [path]: { status: "ready", entries } },
      }));
    })
    .catch((error: unknown) => {
      update(session, (current) => ({
        ...current,
        listings: {
          ...current.listings,
          [path]: {
            status: "error",
            message: error instanceof Error ? error.message : String(error),
          },
        },
      }));
    });
}

registerSessionFeature((session) => {
  setStates((previous) => new Map(previous).set(session, EMPTY));
  const files = session.feature("files");
  const offIndex = files.on<{ root: string; home?: string; files: string[]; pending?: boolean }>(
    "index",
    (message) => {
      update(session, (current) => {
        if (message.pending === true && message.root === current.index.root) {
          return current;
        }
        return {
          index: {
            root: message.root,
            home: message.home ?? null,
            files: message.files,
            pending: message.pending === true,
          },
          listings: message.root === current.index.root ? current.listings : {},
        };
      });
    },
  );
  return () => {
    offIndex();
    setStates((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});
