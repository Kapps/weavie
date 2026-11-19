import { createSignal } from "solid-js";
import { type FileEntry, type FileRow, fileRow } from "../chrome/file-search";
import { canonicalFsPath } from "../editor/fs-path";
import type { FileIndexChange } from "./file-index-sync";
import type { PathTreeNode } from "./path-tree";

interface Slotted extends FileEntry {
  slot: number;
  folderSlot: number;
}

interface Folder {
  /** The names of the folders directly inside this one. */
  readonly folders: Set<string>;
  readonly files: Slotted[];
  /** This folder's level of the tree, kept until what sits directly inside it changes. */
  level: PathTreeNode<string>[] | null;
}

const parentOf = (key: string): string => key.slice(0, Math.max(0, key.lastIndexOf("/")));
const nameOf = (key: string): string => key.slice(key.lastIndexOf("/") + 1);
const byName = new Intl.Collator().compare;

// Where a file sits in the tree. The tree splits on either separator, as `pathAncestorKeys` does, so a POSIX
// name holding a backslash is filed under the same folders that reveal it.
function treePlace(row: FileRow): { folder: string; name: string; key: string } {
  if (!row.rel.includes("\\")) return { folder: row.dir, name: row.leaf, key: row.rel };
  const key = row.rel
    .split(/[\\/]/)
    .filter((segment) => segment.length > 0)
    .join("/");
  return { folder: parentOf(key), name: nameOf(key), key };
}

// A POSIX file name can hold a backslash; a spelling from a Windows host uses one as its separator.
const looseKey = (lower: string): string =>
  lower.includes("\\") ? lower.replaceAll("\\", "/") : lower;

/** A set of listed files, built up path by path so a whole list can be prepared without blocking a frame. */
export class FileListing {
  readonly entries: Slotted[] = [];
  // Keyed by lowercased, slash-separated path: one map answers exact lookups and a recent file's looser spelling.
  private readonly byLower = new Map<string, Slotted | Slotted[]>();
  // Each folder's own files and child folder names, so a tree shows one level without visiting the rest.
  private readonly folders = new Map<string, Folder>([
    ["", { folders: new Set(), files: [], level: null }],
  ]);
  private readonly prefix: string;
  private readonly backslashed: boolean;

  /** `separator` is the host's own: a backslash in a POSIX root is part of a name. */
  constructor(root: string, separator: string) {
    this.backslashed = separator === "\\";
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

  /** What sits directly inside the folder at `key` ("" is the root): folders first, each by name. */
  children(key: string): PathTreeNode<string>[] {
    const folder = this.folders.get(key);
    if (folder === undefined) return [];
    folder.level ??= this.level(key, folder);
    return folder.level;
  }

  private level(key: string, folder: Folder): PathTreeNode<string>[] {
    const below = key === "" ? "" : `${key}/`;
    const children = (childKey: string) => this.children(childKey);
    return [
      ...[...folder.folders].sort(byName).map(
        (name): PathTreeNode<string> => ({
          kind: "directory",
          name,
          key: below + name,
          // Read only for a folder the tree has expanded.
          get children() {
            return children(below + name);
          },
        }),
      ),
      ...folder.files
        .map(({ row }): PathTreeNode<string> => {
          const place = treePlace(row);
          return { kind: "file", name: place.name, key: place.key, value: row.abs };
        })
        .sort((left, right) => byName(left.name, right.name)),
    ];
  }

  // Not recursive: a path can nest deeper than the stack.
  private folder(key: string): Folder {
    const missing: string[] = [];
    let folder = this.folders.get(key);
    for (let at = key; folder === undefined; folder = this.folders.get(at)) {
      missing.push(at);
      at = parentOf(at);
    }
    for (const created of missing.reverse()) {
      folder.folders.add(nameOf(created));
      folder.level = null;
      folder = { folders: new Set(), files: [], level: null };
      this.folders.set(created, folder);
    }
    return folder;
  }

  add(rel: string): void {
    const row = fileRow(this.prefix + (this.backslashed ? rel.replaceAll("/", "\\") : rel), rel);
    const folder = this.folder(treePlace(row).folder);
    const entry: Slotted = {
      row,
      lower: rel.toLowerCase(),
      slot: this.entries.length,
      folderSlot: folder.files.length,
    };
    folder.files.push(entry);
    folder.level = null;
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
    let at = treePlace(entry.row).folder;
    let folder = this.folders.get(at) as Folder;
    const lastInFolder = folder.files.pop() as Slotted;
    if (lastInFolder !== entry) {
      lastInFolder.folderSlot = entry.folderSlot;
      folder.files[entry.folderSlot] = lastInFolder;
    }
    folder.level = null;
    // A folder is listed only while a file sits somewhere beneath it.
    while (at !== "" && folder.files.length + folder.folders.size === 0) {
      this.folders.delete(at);
      folder = this.folders.get(parentOf(at)) as Folder;
      folder.folders.delete(nameOf(at));
      folder.level = null;
      at = parentOf(at);
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
  private readonly workspaceSignal = createSignal<{
    root: string;
    home: string | null;
    separator: string;
  } | null>(null);
  private readonly statusSignal = createSignal<"loading" | "failed" | "listed">("loading");
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
    return this.statusSignal[0]() === "loading";
  }

  /** What a surface with no files to show says: why there are none yet, or `none` once the list is in hand. */
  emptyText(none: string): string {
    const status = this.statusSignal[0]();
    return status === "loading"
      ? "Loading files…"
      : status === "failed"
        ? "Couldn't load files"
        : none;
  }

  /** A download started or failed; either matters only while there is no list to show. */
  setDownloading(failed: boolean): void {
    if (this.listing === null) this.statusSignal[1](failed ? "failed" : "loading");
  }

  get version(): number {
    return this.versionSignal[0]();
  }

  get entries(): readonly FileEntry[] {
    return this.listing?.entries ?? [];
  }

  /** The top level of the file tree; each folder lists its own level only when read. */
  tree(): PathTreeNode<string>[] {
    return this.listing?.children("") ?? [];
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

  /** `separator` is the one the host's paths use, which the root alone does not tell. */
  setWorkspace(root: string, home: string | null, separator: string): void {
    if (this.root !== root || this.home !== home)
      this.workspaceSignal[1]({ root, home, separator });
  }

  /** An empty listing for this workspace, to fill and then {@link install}. */
  newListing(): FileListing {
    const workspace = this.workspaceSignal[0]();
    if (workspace === null) throw new Error("A file list arrived before its workspace.");
    return new FileListing(workspace.root, workspace.separator);
  }

  install(listing: FileListing): void {
    this.listing = listing;
    this.statusSignal[1]("listed");
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
