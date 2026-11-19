import { createSignal } from "solid-js";
import { type FileEntry, type FileRow, fileRow } from "../chrome/file-search";
import { canonicalFsPath } from "../editor/fs-path";
import type { FileIndexChange } from "./file-index-sync";

interface Slotted extends FileEntry {
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

  /** Whether every path the change drops is listed and every path it adds is not. */
  fits(change: FileIndexChange): boolean {
    const removed = new Set(change.removed);
    const added = new Set(change.added);
    return (
      removed.size === change.removed.length &&
      added.size === change.added.length &&
      change.removed.every((rel) => this.lists(rel)) &&
      change.added.every((rel) => removed.has(rel) || !this.lists(rel))
    );
  }

  private lists(rel: string): boolean {
    return this.spellings(rel).some((entry) => entry.row.rel === rel);
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
    const entry = spellings.find((candidate) => candidate.row.rel === rel) as Slotted;
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

  get entries(): readonly FileEntry[] {
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
    if (this.root !== root || this.home !== home) this.workspaceSignal[1]({ root, home });
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

  /** Steps the list by one change, or leaves it untouched and answers false when the change does not fit it. */
  apply(change: FileIndexChange): boolean {
    if (this.listing === null || !this.listing.fits(change)) return false;
    for (const rel of change.removed) this.listing.remove(rel);
    for (const rel of change.added) this.listing.add(rel);
    if (change.added.length + change.removed.length > 0) {
      this.versionSignal[1]((version) => version + 1);
    }
    return true;
  }
}
