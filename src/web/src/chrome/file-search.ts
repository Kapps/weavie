import { Fzf } from "fzf";
import { canonicalFsPath } from "../editor/fs-path";

/// A workspace file split for display and fuzzy ranking. `leafStart` is the offset of `leaf` within `rel`,
/// mapping match positions (indices into `rel`) onto the leaf/dir spans.
export interface FileRow {
  abs: string;
  rel: string;
  leaf: string;
  dir: string;
  leafStart: number;
}

/// A ranked file match carrying the fuzzy-match positions (indices into `rel`) for highlighting.
export interface ScoredFile {
  row: FileRow;
  positions?: Set<number>;
}

// One file plus its lowercased `rel`, precomputed once so the per-keystroke pre-filter scans raw char codes
// without re-lowercasing 100k strings on every query.
interface Entry {
  row: FileRow;
  lower: string;
}

/// A prebuilt index over a fixed file set; rebuild it (via `createFileFinder`) only when the index changes,
/// then query it repeatedly with `rankFiles`.
export interface FileFinder {
  entries: readonly Entry[];
}

export function createFileFinder(rows: readonly FileRow[]): FileFinder {
  return { entries: rows.map((row) => ({ row, lower: row.rel.toLowerCase() })) };
}

/// Splits an absolute path into a {@link FileRow} for display + fuzzy ranking, made repo-relative when it sits
/// under `root` (else the absolute path is used as-is). `leafStart` maps match positions onto the leaf/dir spans.
export function splitPath(abs: string, root: string): FileRow {
  let rel = abs;
  if (root.length > 0 && abs.toLowerCase().startsWith(root.toLowerCase())) {
    rel = abs.slice(root.length).replace(/^[\\/]+/, "");
  }
  const norm = rel.replace(/\\/g, "/");
  const slash = norm.lastIndexOf("/");
  return {
    abs,
    rel: norm,
    leaf: slash >= 0 ? norm.slice(slash + 1) : norm,
    dir: slash >= 0 ? norm.slice(0, slash) : "",
    leafStart: slash >= 0 ? slash + 1 : 0,
  };
}

// Above this many pre-filter survivors, only the best `RESCORE_CAP` are precision-scored. A query that narrows
// to at or under the cap is scored exactly as if the whole index were scored; only ultra-broad queries (which
// match more files than anyone scrolls and that you narrow by typing more) fall back to the heuristic cut.
const RESCORE_CAP = 2000;

interface PreFiltered {
  row: FileRow;
  pre: number;
  index: number;
}

function isWorse(a: PreFiltered, b: PreFiltered): boolean {
  return a.pre > b.pre || (a.pre === b.pre && a.index > b.index);
}

function siftUp(heap: PreFiltered[], start: number): void {
  let child = start;
  while (child > 0) {
    const parent = (child - 1) >> 1;
    if (!isWorse(heap[child] as PreFiltered, heap[parent] as PreFiltered)) {
      return;
    }
    [heap[parent], heap[child]] = [heap[child] as PreFiltered, heap[parent] as PreFiltered];
    child = parent;
  }
}

function siftDown(heap: PreFiltered[]): void {
  let parent = 0;
  while (true) {
    const left = parent * 2 + 1;
    if (left >= heap.length) {
      return;
    }
    const right = left + 1;
    const worseChild =
      right < heap.length && isWorse(heap[right] as PreFiltered, heap[left] as PreFiltered)
        ? right
        : left;
    if (!isWorse(heap[worseChild] as PreFiltered, heap[parent] as PreFiltered)) {
      return;
    }
    [heap[parent], heap[worseChild]] = [
      heap[worseChild] as PreFiltered,
      heap[parent] as PreFiltered,
    ];
    parent = worseChild;
  }
}

// Cheap subsequence pre-filter + ranking hint (lower is better), the inner loop of the per-keystroke scan. Runs
// over every file, so it does no allocation: a two-pointer subsequence test, leaf first so it correlates with
// fzf's basename preference (a file search means the filename), then the whole path. Returns -1 for no match.
function preFilter(hay: string, needle: string, leafStart: number): number {
  const hl = hay.length;
  const nl = needle.length;
  let h = leafStart;
  let n = 0;
  let first = -1;
  let last = -1;
  while (h < hl && n < nl) {
    if (hay.charCodeAt(h) === needle.charCodeAt(n)) {
      if (n === 0) {
        first = h;
      }
      last = h;
      n++;
    }
    h++;
  }
  // Matched within the leaf: rank by how far into the filename it starts, then span (contiguity), then length.
  if (n === nl) {
    return (first - leafStart) * 1e4 + (last - first) * 1e2 + hl / 1000;
  }
  h = 0;
  n = 0;
  first = -1;
  last = -1;
  while (h < hl && n < nl) {
    if (hay.charCodeAt(h) === needle.charCodeAt(n)) {
      if (n === 0) {
        first = h;
      }
      last = h;
      n++;
    }
    h++;
  }
  // Matched only across the directory + name: always ranked below any leaf match (the 1e9 base).
  if (n === nl) {
    return 1e9 + first * 1e2 + (last - first) + hl / 1000;
  }
  return -1;
}

// How far into the filename the match begins: 0 means the filename itself starts with the query. A match that
// begins in the directory portion sorts last — for a file search the basename is what you mean. fzf scores a
// boundary hit the same whether it's "Sess" in SessionSlot or the "sess" after the hyphen in editor-session,
// so without this tiebreak those tie and fall back to raw path length, burying the files you actually meant.
function leafOffset(item: FileRow, positions: Set<number>): number {
  const first = Math.min(...positions);
  return first < item.leafStart ? Number.MAX_SAFE_INTEGER : first - item.leafStart;
}

/// The repo-relative directory of the active file, in {@link FileRow.dir} form, for proximity ranking —
/// null when nothing is open.
export function activeDir(currentFile: string | null, root: string): string | null {
  return currentFile === null ? null : splitPath(currentFile, root).dir;
}

// Tree distance between a candidate's directory and the active file's directory (hops up plus hops down
// through their nearest common ancestor): 0 = same folder, 1 = parent or direct child, and so on. Segments
// compare case-insensitively so differently-cased spellings of the same folder count as the same place.
function dirDistance(dir: string, activeSegs: readonly string[]): number {
  const segs = dir.length === 0 ? [] : dir.toLowerCase().split("/");
  let common = 0;
  while (
    common < segs.length &&
    common < activeSegs.length &&
    segs[common] === activeSegs[common]
  ) {
    common++;
  }
  return segs.length + activeSegs.length - 2 * common;
}

/// Fuzzy-ranks the finder's files against `query` (best-first, uncapped). `recent` is most-recent-first
/// absolute paths; `currentDir` is the active file's directory (see {@link activeDir}), or null when nothing
/// is open. Match quality stays primary; ties then break by where the match lands (filename-start beats
/// mid-name beats directory-only), then by proximity to the active file, then recency, then path length — so
/// among equally good matches the one beside the file you're in surfaces first.
//
// Two phases keep this O(survivors) per keystroke instead of fzf's O(index): a cheap subsequence scan
// (`preFilter`) reduces the index to the matching files, then fzf's precision scorer (and its highlight
// positions) runs over at most `RESCORE_CAP` of them rather than the entire workspace.
export function rankFiles(
  finder: FileFinder,
  query: string,
  recent: readonly string[],
  currentDir: string | null,
): ScoredFile[] {
  const needle = query.toLowerCase();
  const matched: PreFiltered[] = [];
  let matchCount = 0;
  for (let index = 0; index < finder.entries.length; index++) {
    const entry = finder.entries[index] as Entry;
    const pre = preFilter(entry.lower, needle, entry.row.leafStart);
    if (pre >= 0) {
      matchCount++;
      const candidate = { row: entry.row, pre, index };
      if (matched.length < RESCORE_CAP) {
        matched.push(candidate);
        siftUp(matched, matched.length - 1);
      } else if (isWorse(matched[0] as PreFiltered, candidate)) {
        matched[0] = candidate;
        siftDown(matched);
      }
    }
  }
  if (matchCount > RESCORE_CAP) {
    matched.sort((a, b) => a.pre - b.pre || a.index - b.index);
  } else {
    matched.sort((a, b) => a.index - b.index);
  }
  const candidates = matched.map((m) => m.row);

  const fzf = new Fzf(candidates, { selector: (r) => r.rel, casing: "case-insensitive" });
  const rank = new Map(recent.map((abs, i) => [canonicalFsPath(abs), i] as const));
  const activeSegs =
    currentDir === null ? null : currentDir.length === 0 ? [] : currentDir.toLowerCase().split("/");
  // Compute each tiebreak key once per match, then sort — not inside the comparator, which would recompute
  // leafOffset and canonicalFsPath O(n log n) times.
  return fzf
    .find(query)
    .map((r) => ({
      row: r.item,
      positions: r.positions,
      score: r.score,
      leaf: leafOffset(r.item, r.positions),
      proximity: activeSegs === null ? 0 : dirDistance(r.item.dir, activeSegs),
      recency: rank.get(canonicalFsPath(r.item.abs)) ?? Number.MAX_SAFE_INTEGER,
    }))
    .sort(
      (a, b) =>
        b.score - a.score ||
        a.leaf - b.leaf ||
        a.proximity - b.proximity ||
        a.recency - b.recency ||
        a.row.rel.length - b.row.rel.length,
    )
    .map(({ row, positions }) => ({ row, positions }));
}
