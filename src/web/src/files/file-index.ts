import { createSignal } from "solid-js";
import {
  type ClientSession,
  registerSessionFeature,
  selectedSession,
  sessionResourceUrl,
} from "../bridge";
import { type FileRow, fileRow } from "../chrome/file-search";
import { canonicalFsPath } from "../editor/fs-path";
import { readJsonStream } from "../messaging/json-stream";
import { createSessionOwnedState } from "../messaging/session-owned-state";
import { clearNotification, notify } from "../notify/notify";
import { type FileIndexChange, FileIndexSync } from "./file-index-sync";

/** One listed file, with what the per-keystroke search scan needs precomputed. */
export interface FileIndexEntry {
  readonly row: FileRow;
  readonly lower: string;
}

interface Slotted extends FileIndexEntry {
  slot: number;
}

// A POSIX file name can hold a backslash; a spelling from a Windows host uses one as its separator.
const looseKey = (lower: string): string =>
  lower.includes("\\") ? lower.replaceAll("\\", "/") : lower;

/** A set of listed files, built up path by path so a whole list can be prepared without blocking a frame. */
export class FileListing {
  readonly entries: Slotted[] = [];
  // Keyed by lowercased, slash-separated path: one map answers exact lookups and a recent file's looser spelling.
  private readonly byLower = new Map<string, Slotted | Slotted[]>();
  private readonly prefix: string;
  private readonly backslashed: boolean;

  constructor(root: string) {
    this.backslashed = root.includes("\\");
    const separator = this.backslashed ? "\\" : "/";
    this.prefix = canonicalFsPath(root) + (root.endsWith(separator) ? "" : separator);
  }

  spellings(rel: string): Slotted[] {
    const found = this.byLower.get(looseKey(rel.toLowerCase()));
    return found === undefined ? [] : Array.isArray(found) ? found : [found];
  }

  add(rel: string): void {
    const entry: Slotted = {
      row: fileRow(this.prefix + (this.backslashed ? rel.replaceAll("/", "\\") : rel), rel),
      lower: rel.toLowerCase(),
      slot: this.entries.length,
    };
    this.entries.push(entry);
    const key = looseKey(entry.lower);
    const existing = this.byLower.get(key);
    if (existing === undefined) this.byLower.set(key, entry);
    else if (Array.isArray(existing)) existing.push(entry);
    else this.byLower.set(key, [existing, entry]);
  }

  remove(rel: string): void {
    const spellings = this.spellings(rel);
    const entry = spellings.find((candidate) => candidate.row.rel === rel);
    if (entry === undefined)
      throw new Error(`The host removed a file this page never listed: ${rel}`);
    const last = this.entries.pop() as Slotted;
    if (last !== entry) {
      last.slot = entry.slot;
      this.entries[entry.slot] = last;
    }
    const rest = spellings.filter((candidate) => candidate !== entry);
    const key = looseKey(entry.lower);
    if (rest.length === 0) this.byLower.delete(key);
    else this.byLower.set(key, rest.length === 1 ? (rest[0] as Slotted) : rest);
  }
}

/**
 * One session's file list, kept in step with the host by changes rather than re-sent. Reads of `version` re-run
 * when the list changes; `entries` is in no particular order.
 */
export class FileIndex {
  private listing: FileListing | null = null;
  private readonly workspaceSignal = createSignal<{ root: string; home: string | null } | null>(
    null,
  );
  private readonly pendingSignal = createSignal(true);
  private readonly versionSignal = createSignal(0);

  get root(): string | null {
    return this.workspaceSignal[0]()?.root ?? null;
  }

  /** The host's home directory, so `~/…` in open-by-path expands against the host, not the browser. */
  get home(): string | null {
    return this.workspaceSignal[0]()?.home ?? null;
  }

  /** True until the first list has arrived. */
  get pending(): boolean {
    return this.pendingSignal[0]();
  }

  get version(): number {
    return this.versionSignal[0]();
  }

  get entries(): readonly FileIndexEntry[] {
    return this.listing?.entries ?? [];
  }

  /** The file at exactly this workspace-relative path. */
  find(rel: string): FileRow | undefined {
    return this.listing?.spellings(rel).find((entry) => entry.row.rel === rel)?.row;
  }

  /** The file at this path when case is ignored, provided only one is. */
  findLoose(rel: string): FileRow | undefined {
    const spellings = this.listing?.spellings(rel) ?? [];
    return spellings.length === 1 ? spellings[0]?.row : undefined;
  }

  setWorkspace(root: string, home: string | null): void {
    this.workspaceSignal[1]({ root, home });
  }

  /** An empty listing for this workspace, to fill and then {@link install}. */
  newListing(): FileListing {
    const root = this.root;
    if (root === null) throw new Error("A file list arrived before its workspace.");
    return new FileListing(root);
  }

  install(listing: FileListing): void {
    this.listing = listing;
    this.pendingSignal[1](false);
    this.versionSignal[1]((version) => version + 1);
  }

  apply(change: FileIndexChange): void {
    if (this.listing === null) throw new Error("A file list change arrived before its list.");
    for (const rel of change.removed) this.listing.remove(rel);
    for (const rel of change.added) this.listing.add(rel);
    if (change.added.length + change.removed.length > 0) {
      this.versionSignal[1]((version) => version + 1);
    }
  }
}

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
    void load(current.signal).catch((error: unknown) => {
      if (current.signal.aborted) return;
      sync.abandon();
      notify("error", `Couldn't load the file list: ${String(error)}`, errorKey);
    });
  };

  const load = async (signal: AbortSignal): Promise<void> => {
    const url = sessionResourceUrl(session, "/weavie-file-index");
    if (sync.known !== null) url.searchParams.set("knownRevision", sync.known.toString());
    const listing = index.newListing();
    for await (const batch of readJsonStream<IndexBatch>(await fetch(url, { signal }))) {
      signal.throwIfAborted();
      for (const path of batch.paths) listing.add(path);
      if (!batch.complete) continue;
      if (!batch.unchanged) {
        if (listing.entries.length !== batch.count) {
          throw new Error("The file list has an incorrect path count.");
        }
        index.install(listing);
      }
      clearNotification(errorKey);
      sync.complete(batch.revision, !batch.unchanged);
      return;
    }
    throw new Error("The file list ended before completion.");
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
