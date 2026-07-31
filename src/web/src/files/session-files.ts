import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import type { DirListings } from "./FileBrowser";

interface FileIndex {
  root: string | null;
  files: string[];
  pending: boolean;
}

interface SessionFiles {
  index: FileIndex;
  listings: DirListings;
}

const EMPTY: SessionFiles = {
  index: { root: null, files: [], pending: false },
  listings: {},
};
const [states, setStates] = createSignal<Map<ClientSession, SessionFiles>>(new Map());

function update(session: ClientSession, mutate: (current: SessionFiles) => SessionFiles): void {
  setStates((previous) => {
    const next = new Map(previous);
    next.set(session, mutate(previous.get(session) ?? EMPTY));
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
  selectedSession()?.feature("files").publish("listDirectory", { path });
}

registerSessionFeature((session) => {
  const files = session.feature("files");
  const offIndex = files.on<{ root: string; files: string[]; pending?: boolean }>(
    "index",
    (message) => {
      update(session, (current) => {
        if (message.pending === true && message.root === current.index.root) {
          return current;
        }
        return {
          index: {
            root: message.root,
            files: message.files,
            pending: message.pending === true,
          },
          listings: message.root === current.index.root ? current.listings : {},
        };
      });
    },
  );
  const offDirectory = files.on<{
    path: string;
    entries: DirListings[string];
  }>("directory", ({ path, entries }) => {
    update(session, (current) => ({
      ...current,
      listings: { ...current.listings, [path]: entries },
    }));
  });
  return () => {
    offIndex();
    offDirectory();
    setStates((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});
