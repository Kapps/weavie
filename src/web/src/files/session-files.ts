import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import { normalizePath } from "../editor/fs-path";
import { PAGE_EPOCH } from "../messaging/page-epoch";
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
interface DirectorySubscription {
  id: string;
  path: string | null;
  consumers: number;
  request: { again: boolean } | null;
}

export interface DirectoryLease {
  refresh(): void;
  release(): void;
}

const directories = createSessionOwnedState(() => new Map<string, DirectorySubscription>());
let directorySequence = 0;

function updateListings(
  session: ClientSession,
  mutate: (current: DirListings) => DirListings,
): void {
  listings.update(session, mutate);
}

export const fileIndexFor = (session: ClientSession | null): FileIndex =>
  indexes.get(session) ?? EMPTY_INDEX;
export const selectedFileIndex = (): FileIndex => fileIndexFor(selectedSession());
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

/** Retains a listing and its watch until this session's last consumer releases it. */
export function acquireDirectory(session: ClientSession, path: string): DirectoryLease {
  const owned = directories.get(session);
  if (owned === undefined) throw new Error("Cannot list a directory in a closed session.");
  let subscription = owned.get(path);
  if (subscription === undefined) {
    subscription = {
      id: `${PAGE_EPOCH}-directory-${++directorySequence}`,
      path: null,
      consumers: 0,
      request: null,
    };
    owned.set(path, subscription);
  }
  const entry = subscription;
  entry.consumers += 1;
  if (entry.consumers === 1) listDirectory(session, path, entry);
  let released = false;
  return {
    refresh: () => {
      if (!released) listDirectory(session, path, entry);
    },
    release: () => {
      if (released) return;
      released = true;
      entry.consumers -= 1;
      if (entry.consumers > 0 || session.closed) return;
      owned.delete(path);
      session.feature("files").publish("unwatchDirectory", { subscriptionId: entry.id });
      updateListings(session, (current) => {
        const { [path]: _removed, ...rest } = current;
        return rest;
      });
    },
  };
}

function listDirectory(
  session: ClientSession,
  path: string,
  subscription: DirectorySubscription,
): void {
  const current = () => !session.closed && directories.get(session)?.get(path) === subscription;
  if (!current()) return;
  if (subscription.request !== null) {
    subscription.request.again = true;
    return;
  }
  const request = { again: false };
  subscription.request = request;
  if (listings.get(session)?.[path]?.status !== "ready") {
    updateListings(session, (current) => ({ ...current, [path]: { status: "loading" } }));
  }
  void (async () => {
    do {
      request.again = false;
      try {
        const { entries, path: canonicalPath } = await session
          .feature("files")
          .request<{ path: string; entries: DirEntry[] }, { path: string; subscriptionId: string }>(
            "listDirectory",
            { path, subscriptionId: subscription.id },
          );
        if (!current()) return;
        subscription.path = canonicalPath;
        if (request.again) continue;
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
        if (!current()) return;
        if (!request.again)
          updateListings(session, (current) => ({
            ...current,
            [path]: {
              status: "error",
              message: error instanceof Error ? error.message : String(error),
            },
          }));
      }
    } while (request.again && current());
    if (current()) subscription.request = null;
  })();
}

registerSessionFeature((session) => {
  const files = session.feature("files");
  files.publish("reset", { pageEpoch: PAGE_EPOCH });
  const offChanged = files.on<{ changes: { path: string }[] }>("changed", ({ changes }) => {
    const changed = changes.map((change) => normalizePath(change.path));
    for (const [path, subscription] of directories.get(session) ?? []) {
      const directory = subscription.path === null ? null : normalizePath(subscription.path);
      if (
        changed.some(
          (change) =>
            directory === null ||
            change === directory ||
            change.startsWith(`${directory}/`) ||
            directory.startsWith(`${change}/`),
        )
      ) {
        listDirectory(session, path, subscription);
      }
    }
  });
  const offIndex = files.on<{ root: string; home?: string; files: string[]; pending?: boolean }>(
    "index",
    (message) => {
      const previousRoot = indexes.get(session)?.root ?? null;
      if (message.pending === true && message.root === previousRoot) {
        for (const [path, subscription] of directories.get(session) ?? [])
          listDirectory(session, path, subscription);
        return;
      }
      indexes.update(session, () => ({
        root: message.root,
        // An empty profile path is no answer: `~` stays unresolvable rather than naming the root.
        home: message.home === undefined || message.home === "" ? null : message.home,
        files: message.files,
        pending: message.pending === true,
      }));
    },
  );
  return () => {
    offIndex();
    offChanged();
  };
});
