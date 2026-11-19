import { type ClientSession, registerSessionFeature } from "../bridge";
import { normalizePath } from "../editor/fs-path";
import { PAGE_EPOCH } from "../messaging/page-epoch";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import type { DirEntry, DirListings } from "./FileBrowser";

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

export const directoryListingsFor = (session: ClientSession | null): DirListings =>
  listings.get(session) ?? {};

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
  // The host sends the workspace on every sync, and a listing held across a reconnect may have missed changes.
  const offWorkspace = files.on("workspace", () => {
    for (const [path, subscription] of directories.get(session) ?? [])
      listDirectory(session, path, subscription);
  });
  return () => {
    offWorkspace();
    offChanged();
  };
});
