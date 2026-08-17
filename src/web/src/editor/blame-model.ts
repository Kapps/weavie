// The blame data the editor renders, and the pure functions over it: keeping per-line attribution aligned as the
// buffer is edited, and turning one commit into the faded label shown at the end of its line. No Monaco, no DOM,
// so both are unit-tested directly.

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

/** A file's blame: each commit once, plus per-line indices into them. */
export interface BlameSnapshot {
  commits: BlameCommit[];
  /** Per line (index 0 = line 1), the index into `commits`; -1 for a line only this buffer has. */
  lineCommits: number[];
  /** Per line, its number inside the attributed commit; 0 when there is none. */
  lineOriginals: number[];
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

export const EMPTY_BLAME: BlameSnapshot = { commits: [], lineCommits: [], lineOriginals: [] };

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
  // Which line survives the seam decides where to splice. An edit starting mid-line keeps the head of
  // `startLine`, so the removed lines are the ones after it (0-based index `startLine`). An edit starting at
  // column 1 consumes `startLine` whole and leaves the tail of the last removed line in its place, so the
  // range to drop begins at `startLine` itself (index `startLine - 1`) — otherwise the surviving text keeps
  // the attribution of the line that was deleted out from under it.
  const at = edit.fromLineStart ? edit.startLine - 1 : edit.startLine;
  lineCommits.splice(at, edit.removedLines, ...Array<number>(edit.addedLines).fill(LOCAL));
  lineOriginals.splice(at, edit.removedLines, ...Array<number>(edit.addedLines).fill(0));
  return { commits: snapshot.commits, lineCommits, lineOriginals };
}

/**
 * True when `line` begins a run — the line above it belongs to a different commit, or to none. One commit
 * usually owns a stretch of consecutive lines, and repeating its label down every one of them is noise; the
 * label belongs where the change starts.
 */
export function startsRun(snapshot: BlameSnapshot, line: number): boolean {
  const current = snapshot.lineCommits[line - 1];
  return current !== undefined && current !== LOCAL && current !== snapshot.lineCommits[line - 2];
}

/** What `line` (1-based) is attributed to, or null when nothing is — a locally typed or out-of-range line. */
export function blameAt(snapshot: BlameSnapshot, line: number): BlameLine | null {
  const index = snapshot.lineCommits[line - 1];
  const commit = index === undefined || index === LOCAL ? undefined : snapshot.commits[index];
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
