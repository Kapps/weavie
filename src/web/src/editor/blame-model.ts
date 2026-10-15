// The blame data the editor renders, and the pure functions over it: keeping per-line attribution aligned as the
// buffer is edited, filling it in one fetched range at a time, and turning one commit into the faded label shown at
// the end of its line. No Monaco, no DOM, so all of it is unit-tested directly.

/** One commit a blame attributes lines to, as the host's `git.blame` response carries it. */
export interface BlameCommit {
  sha: string;
  author: string;
  email: string;
  /** Author time, seconds since the Unix epoch. */
  time: number;
  summary: string;
  /** True for Git's all-zero sha: the line is in the working tree but no commit. */
  uncommitted: boolean;
}

/**
 * A file's blame as far as it has been fetched: each commit once, plus per-line indices into them. Blame is
 * fetched only for the lines on screen — a whole-file blame of a huge generated file stalls — so most lines of a
 * long file are `UNLOADED` until scrolled to.
 */
export interface BlameSnapshot {
  commits: BlameCommit[];
  /**
   * Per line (index 0 = line 1), the index into `commits`; `LOCAL` for a line no commit has, `UNLOADED` for one
   * never fetched.
   */
  lineCommits: number[];
  /** Per line, its number inside the attributed commit; 0 when there is none. */
  lineOriginals: number[];
  /**
   * Per line, its number in the file on disk — the address blame is fetched by — while it is still to be
   * fetched; 0 once it has been, or for a line only this buffer has.
   */
  unfetched: number[];
}

/** One fetched range of blame, as the host's `git.blame` response carries it. */
export interface BlameRange {
  commits: BlameCommit[];
  /** Per line from the range's first, the index into `commits`; shorter than asked when the file ends first. */
  lineCommits: number[];
  lineOriginals: number[];
}

/** A span of disk lines to fetch blame for, inclusive. */
export interface DiskRange {
  start: number;
  end: number;
}

/** What one line is attributed to. */
export interface BlameLine {
  commit: BlameCommit;
  /** The line's number inside `commit` — the anchor for pulling the hunk it came from. */
  originalLine: number;
}

/** A buffer edit reduced to its effect on line identity. */
export interface LineEdit {
  /** The first line the edit touches. */
  startLine: number;
  /** How many whole lines the edit removed. */
  removedLines: number;
  /** How many new lines the edit inserted. */
  addedLines: number;
  /**
   * True when the edit begins at column 1, so `startLine` is consumed whole rather than partly surviving.
   * It decides which side of the seam keeps its attribution: an edit starting mid-line leaves the head of
   * `startLine` in place, while one starting at its first column leaves only the tail of the last removed line.
   */
  fromLineStart: boolean;
}

/** A line the buffer has but no commit does — typed since the last save, so nothing is attributed to it. */
const LOCAL = -1;

/** A line whose blame hasn't been fetched yet. */
const UNLOADED = -2;

export const EMPTY_BLAME: BlameSnapshot = {
  commits: [],
  lineCommits: [],
  lineOriginals: [],
  unfetched: [],
};

/**
 * Marks every line of `snapshot` to be fetched again — the file changed on disk and the buffer matches it — while
 * each keeps its last-known attribution on screen until the new one lands. When the last line is empty it is the
 * editor's view of the final newline, not a line of the file, so Git is never asked about it.
 */
export function refetchBlame(snapshot: BlameSnapshot, lastLineEmpty: boolean): BlameSnapshot {
  const lineCount = snapshot.lineCommits.length;
  const counted = lastLineEmpty ? lineCount - 1 : lineCount;
  return {
    ...snapshot,
    unfetched: Array.from({ length: lineCount }, (_, index) => (index < counted ? index + 1 : 0)),
  };
}

/** A snapshot of `lineCount` buffer lines matching the file on disk, none fetched yet. */
export function unloadedBlame(lineCount: number, lastLineEmpty: boolean): BlameSnapshot {
  return refetchBlame(
    {
      commits: [],
      lineCommits: Array<number>(lineCount).fill(UNLOADED),
      lineOriginals: Array<number>(lineCount).fill(0),
      unfetched: [],
    },
    lastLineEmpty,
  );
}

/**
 * The first unbroken span of disk lines still to be fetched among buffer lines `from`–`to`; null when none are.
 * It stops where disk lines skip, so deleting a long block never turns a screenful into a fetch of everything
 * the block used to hold.
 */
export function missingRange(snapshot: BlameSnapshot, from: number, to: number): DiskRange | null {
  let range: DiskRange | null = null;
  for (let line = from; line <= to; line++) {
    const disk = snapshot.unfetched[line - 1];
    if (disk === undefined || disk === 0) continue;
    if (range === null) range = { start: disk, end: disk };
    else if (disk === range.end + 1) range.end = disk;
    else break;
  }
  return range;
}

/**
 * Fills the buffer lines still to be fetched inside `requested` with `range`, the blame fetched for it. A line
 * the response stops short of is past the file's end, so no commit has it.
 */
export function mergeBlame(
  snapshot: BlameSnapshot,
  requested: DiskRange,
  range: BlameRange,
): BlameSnapshot {
  const commits = [...snapshot.commits];
  const indexBySha = new Map(commits.map((commit, index) => [commit.sha, index]));
  const remap = range.commits.map((commit) => {
    let index = indexBySha.get(commit.sha);
    if (index === undefined) {
      index = commits.push(commit) - 1;
      indexBySha.set(commit.sha, index);
    }
    return index;
  });
  const lineCommits = [...snapshot.lineCommits];
  const lineOriginals = [...snapshot.lineOriginals];
  const unfetched = [...snapshot.unfetched];
  unfetched.forEach((disk, index) => {
    if (disk < requested.start || disk > requested.end) return;
    const offset = disk - requested.start;
    const commit = range.lineCommits[offset];
    lineCommits[index] = commit === undefined || commit < 0 ? LOCAL : remap[commit]!;
    lineOriginals[index] = range.lineOriginals[offset] ?? 0;
    unfetched[index] = 0;
  });
  return { commits, lineCommits, lineOriginals, unfetched };
}

/**
 * Re-aligns a snapshot after a buffer edit, so annotations stay on their own lines while the file is edited
 * instead of drifting until the next save. Lines the edit introduced become local (unattributed), which is what
 * they are: the commit that will carry them does not exist yet.
 */
export function applyEdit(snapshot: BlameSnapshot, edit: LineEdit): BlameSnapshot {
  if (edit.removedLines === 0 && edit.addedLines === 0) {
    return snapshot;
  }
  const lineCommits = [...snapshot.lineCommits];
  const lineOriginals = [...snapshot.lineOriginals];
  const unfetched = [...snapshot.unfetched];
  // Which line survives the seam decides where to splice. An edit starting mid-line keeps the head of
  // `startLine`, so the removed lines are the ones after it (0-based index `startLine`). An edit starting at
  // column 1 consumes `startLine` whole and leaves the tail of the last removed line in its place, so the
  // range to drop begins at `startLine` itself (index `startLine - 1`) — otherwise the surviving text keeps
  // the attribution of the line that was deleted out from under it.
  const at = edit.fromLineStart ? edit.startLine - 1 : edit.startLine;
  lineCommits.splice(at, edit.removedLines, ...Array<number>(edit.addedLines).fill(LOCAL));
  lineOriginals.splice(at, edit.removedLines, ...Array<number>(edit.addedLines).fill(0));
  unfetched.splice(at, edit.removedLines, ...Array<number>(edit.addedLines).fill(0));
  return { commits: snapshot.commits, lineCommits, lineOriginals, unfetched };
}

/**
 * True when `line` is where its commit's run carries the label — the first line of the run with code on it,
 * per `hasContent`. One commit usually owns a stretch of consecutive lines, and repeating its label down every
 * one of them is noise; a run that opens on blank lines hands the label down to the code it introduced rather
 * than hanging it off nothing. A run reaching up into lines not fetched yet may start up there, so it stays
 * unlabelled until they are.
 */
export function labelsRun(
  snapshot: BlameSnapshot,
  line: number,
  hasContent: (line: number) => boolean,
): boolean {
  const current = snapshot.lineCommits[line - 1];
  if (current === undefined || current < 0 || !hasContent(line)) {
    return false;
  }
  let first = line;
  for (; snapshot.lineCommits[first - 2] === current; first--) {
    if (hasContent(first - 1)) {
      return false;
    }
  }
  return snapshot.lineCommits[first - 2] !== UNLOADED;
}

/**
 * What `line` (1-based) is attributed to, or null when nothing is — a locally typed, not yet fetched, or
 * out-of-range line.
 */
export function blameAt(snapshot: BlameSnapshot, line: number): BlameLine | null {
  const index = snapshot.lineCommits[line - 1];
  const commit = index === undefined || index < 0 ? undefined : snapshot.commits[index];
  return commit === undefined
    ? null
    : { commit, originalLine: snapshot.lineOriginals[line - 1] ?? 0 };
}

// Long enough to recognize the change, short enough not to run past the code it annotates.
const SUMMARY_LIMIT = 68;

/** The faded end-of-line label for one commit, e.g. `Kapps, 3 days ago • Fix the drain race`. */
export function blameLabel(commit: BlameCommit, now: number): string {
  if (commit.uncommitted) {
    return "Uncommitted changes";
  }
  const summary =
    commit.summary.length > SUMMARY_LIMIT
      ? `${commit.summary.slice(0, SUMMARY_LIMIT - 1).trimEnd()}…`
      : commit.summary;
  return `${commit.author}, ${relativeTime(commit.time, now)} • ${summary}`;
}

const MINUTE = 60;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const WEEK = 7 * DAY;
const MONTH = 30 * DAY;
const YEAR = 365 * DAY;

/** A coarse "3 days ago" for a Unix-seconds timestamp, measured against `now` (also Unix seconds). */
export function relativeTime(time: number, now: number): string {
  const seconds = Math.max(now - time, 0);
  if (seconds < MINUTE) {
    return "just now";
  }
  for (const [unit, name] of [
    [YEAR, "year"],
    [MONTH, "month"],
    [WEEK, "week"],
    [DAY, "day"],
    [HOUR, "hour"],
    [MINUTE, "minute"],
  ] as const) {
    if (seconds >= unit) {
      const count = Math.floor(seconds / unit);
      return `${count} ${name}${count === 1 ? "" : "s"} ago`;
    }
  }
  return "just now";
}

/** The short sha a commit is identified by in the popover. */
export function shortSha(sha: string): string {
  return sha.slice(0, 8);
}
