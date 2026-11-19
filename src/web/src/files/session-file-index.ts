import { type ClientSession, registerSessionFeature, sessionResourceUrl } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { readJsonStream } from "../messaging/json-stream";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import { clearNotification, notify } from "../notify/notify";
import { FileIndex } from "./file-index";
import { type FileIndexChange, FileIndexSync } from "./file-index-sync";

const NO_SESSION = new FileIndex();
const indexes = createSessionOwnedState(() => new FileIndex());

export const fileIndexFor = (session: ClientSession | null): FileIndex =>
  indexes.get(session) ?? NO_SESSION;

// Each loaded session's own download, so Reload File List reaches the session it was run for.
const reloads = new Map<ClientSession, () => void>();

registerCommand(CommandIds.reloadFileList, (_args, { session }) => {
  if (session === null) return false;
  reloads.get(session)?.();
});

interface IndexBatch {
  revision: number;
  count: number;
  paths: string[];
  unchanged: boolean;
  complete: boolean;
}

registerSessionFeature((session) => {
  const index = indexes.get(session);
  if (index === undefined) return () => {};
  const files = session.feature("files");
  const errorKey = `file-index:${session.connection.id}:${session.address.incarnation}`;
  let abort: AbortController | null = null;

  const download = (): void => {
    abort?.abort();
    const current = new AbortController();
    abort = current;
    sync.begin();
    index.setDownloading(false);
    void load(current.signal).catch((error: unknown) => {
      if (current.signal.aborted) return;
      const reason = error instanceof Error ? error.message : String(error);
      notify("error", `Couldn't load the file list: ${reason}`, errorKey, {
        label: "Retry",
        run: download,
      });
      index.setDownloading(true);
      sync.abandon();
    });
  };

  const load = async (signal: AbortSignal): Promise<void> => {
    const url = sessionResourceUrl(session, "/weavie-file-index");
    if (sync.known !== null) url.searchParams.set("knownRevision", sync.known.toString());
    const listing = index.newListing();
    const response = await fetch(url, { signal });
    // The host has nothing to list here: the session is closing, or its folder is gone and the host has said so.
    if (response.status === 404) {
      index.setDownloading(true);
      sync.abandon();
      return;
    }
    // Read to the end of the response, so a complete download never ends as a cancelled request.
    let last: IndexBatch | null = null;
    for await (const batch of readJsonStream<IndexBatch>(response)) {
      signal.throwIfAborted();
      if (last?.complete === true) throw new Error("The file list continued past its end.");
      for (const path of batch.paths) listing.add(path);
      last = batch;
    }
    if (last?.complete !== true) throw new Error("The file list ended before completion.");
    if (!last.unchanged) {
      if (listing.entries.length !== last.count) {
        throw new Error("The file list has an incorrect path count.");
      }
      index.install(listing);
    }
    clearNotification(errorKey);
    sync.complete(last.revision, !last.unchanged);
  };

  const sync = new FileIndexSync((change) => index.apply(change), download);
  // The host sends the workspace on every sync, so each connection checks the list it holds against the host's.
  const offWorkspace = files.on<{ root: string; home: string; separator: string }>(
    "workspace",
    ({ root, home, separator }) => {
      index.setWorkspace(root, home === "" ? null : home, separator);
      download();
    },
  );
  reloads.set(session, download);
  const offChanged = files.on<FileIndexChange>("indexChanged", (change) => sync.changed(change));
  const offReplaced = files.on<{ revision: number }>("indexReplaced", ({ revision }) =>
    sync.replaced(revision),
  );
  return () => {
    abort?.abort();
    reloads.delete(session);
    clearNotification(errorKey);
    offWorkspace();
    offChanged();
    offReplaced();
  };
});
