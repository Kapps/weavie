import {
  type ClientSession,
  registerSessionFeature,
  selectedSession,
  sessionResourceUrl,
} from "../bridge";
import { readJsonStream } from "../messaging/json-stream";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import { clearNotification, notify } from "../notify/notify";
import { FileIndex } from "./file-index";
import { type FileIndexChange, FileIndexSync } from "./file-index-sync";

const NO_SESSION = new FileIndex();
const indexes = createSessionOwnedState(() => new FileIndex());

export const fileIndexFor = (session: ClientSession | null): FileIndex =>
  indexes.get(session) ?? NO_SESSION;
export const selectedFileIndex = (): FileIndex => fileIndexFor(selectedSession());

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
    // The host no longer has this session loaded, so this page's copy of it is about to be released too.
    if (response.status === 404) {
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
  const offWorkspace = files.on<{ root: string; home: string }>("workspace", ({ root, home }) => {
    index.setWorkspace(root, home === "" ? null : home);
    download();
  });
  const offChanged = files.on<FileIndexChange>("indexChanged", (change) => sync.changed(change));
  const offReplaced = files.on<{ revision: number }>("indexReplaced", ({ revision }) =>
    sync.replaced(revision),
  );
  return () => {
    abort?.abort();
    clearNotification(errorKey);
    offWorkspace();
    offChanged();
    offReplaced();
  };
});
