import { describe, expect, it } from "vitest";
import {
  applyEdit,
  type BlameCommit,
  type BlameSnapshot,
  blameAt,
  blameLabel,
  labelsRun,
  mergeBlame,
  missingRange,
  refetchBlame,
  relativeTime,
  unloadedBlame,
} from "./blame-model";

function commit(sha: string, over: Partial<BlameCommit> = {}): BlameCommit {
  return {
    sha,
    author: "Kapps",
    email: "kapps@example.com",
    time: 1_700_000_000,
    summary: `subject ${sha}`,
    uncommitted: false,
    ...over,
  };
}

// One distinct commit per line, so a test can tell which line survived a seam — with a shared commit the
// mis-attribution this guards against is invisible.
function snapshot(): BlameSnapshot {
  return {
    commits: [commit("aaa"), commit("bbb"), commit("ccc")],
    lineCommits: [0, 1, 2],
    lineOriginals: [1, 2, 3],
    unfetched: [1, 2, 3],
  };
}

const shas = (s: BlameSnapshot): (string | null)[] =>
  s.lineCommits.map((_, index) => blameAt(s, index + 1)?.commit.sha ?? null);

describe("applyEdit", () => {
  it("keeps every line's attribution when nothing changes line count", () => {
    // Typing within line 2: the range starts and ends on it, so no whole line is added or removed.
    const next = applyEdit(snapshot(), {
      startLine: 2,
      removedLines: 0,
      addedLines: 0,
      fromLineStart: false,
    });

    expect(shas(next)).toEqual(["aaa", "bbb", "ccc"]);
  });

  it("splitting a line mid-way keeps the head's attribution and leaves the new line unattributed", () => {
    const next = applyEdit(snapshot(), {
      startLine: 1,
      removedLines: 0,
      addedLines: 2,
      fromLineStart: false,
    });

    expect(shas(next)).toEqual(["aaa", null, null, "bbb", "ccc"]);
  });

  it("inserting at the start of a line pushes that line down rather than displacing it", () => {
    // Pasting above line 2: the new lines take its place and it moves down still wearing its own commit.
    const next = applyEdit(snapshot(), {
      startLine: 2,
      removedLines: 0,
      addedLines: 2,
      fromLineStart: true,
    });

    expect(shas(next)).toEqual(["aaa", null, null, "bbb", "ccc"]);
  });

  it("deleting whole lines leaves the surviving line wearing its own commit", () => {
    // Select from the start of line 1 to the start of line 3 and delete: lines 1-2 go, line 3 survives as
    // line 1. It must keep "ccc" — the seam is where a naive splice hands it the deleted line's commit.
    const next = applyEdit(snapshot(), {
      startLine: 1,
      removedLines: 2,
      addedLines: 0,
      fromLineStart: true,
    });

    expect(shas(next)).toEqual(["ccc"]);
  });

  it("keeps the head's attribution when a mid-line selection spanning lines is replaced", () => {
    // From mid-line 1 to mid-line 3: the merged line begins with line 1's text, so it keeps "aaa".
    const next = applyEdit(snapshot(), {
      startLine: 1,
      removedLines: 2,
      addedLines: 0,
      fromLineStart: false,
    });

    expect(shas(next)).toEqual(["aaa"]);
  });

  it("carries each line's original number with it", () => {
    const next = applyEdit(snapshot(), {
      startLine: 1,
      removedLines: 0,
      addedLines: 1,
      fromLineStart: false,
    });

    expect(blameAt(next, 1)?.originalLine).toBe(1);
    expect(blameAt(next, 2)).toBeNull();
    expect(blameAt(next, 3)?.originalLine).toBe(2);
  });
});

describe("labelsRun", () => {
  // Five lines: one commit owns 1-3, another 4-5 — the shape that made annotating every line unreadable.
  const runs: BlameSnapshot = {
    commits: [commit("aaa"), commit("bbb")],
    lineCommits: [0, 0, 0, 1, 1],
    lineOriginals: [1, 2, 3, 4, 5],
    unfetched: [1, 2, 3, 4, 5],
  };
  const filled = (): boolean => true;
  // Lines given as text, so a case reads as the file it describes.
  const content =
    (lines: string[]) =>
    (line: number): boolean =>
      (lines[line - 1] ?? "").trim().length > 0;

  it("marks only the first line of each run", () => {
    expect([1, 2, 3, 4, 5].map((line) => labelsRun(runs, line, filled))).toEqual([
      true,
      false,
      false,
      true,
      false,
    ]);
  });

  it("restarts a run that a locally typed line interrupts", () => {
    const typed: BlameSnapshot = {
      commits: [commit("aaa")],
      lineCommits: [0, -1, 0],
      lineOriginals: [1, 0, 2],
      unfetched: [1, 0, 2],
    };

    // The line below the insert opens a new run: its neighbour above belongs to no commit.
    expect([1, 2, 3].map((line) => labelsRun(typed, line, filled))).toEqual([true, false, true]);
  });

  // A commit that appended a block owns the blank separator above it, so its run opens on empty lines.
  const blankOpening: BlameSnapshot = {
    commits: [commit("aaa"), commit("bbb")],
    lineCommits: [0, 1, 1, 1],
    lineOriginals: [1, 2, 3, 4],
    unfetched: [1, 2, 3, 4],
  };

  it("hands the label down to the run's first line with code on it", () => {
    const lines = ["const kept = 1;", "", "  ", "const added = 2;"];

    // The label belongs on the code the commit introduced, not on the empty lines above it — and it is still
    // shown exactly once for the run.
    expect([1, 2, 3, 4].map((line) => labelsRun(blankOpening, line, content(lines)))).toEqual([
      true,
      false,
      false,
      true,
    ]);
  });

  it("labels nothing when a whole run is blank", () => {
    const lines = ["const kept = 1;", "", "", ""];

    expect([2, 3, 4].map((line) => labelsRun(blankOpening, line, content(lines)))).toEqual([
      false,
      false,
      false,
    ]);
  });

  it("leaves a run unlabelled while the lines above it aren't fetched", () => {
    const partial = mergeBlame(
      unloadedBlame(4, false),
      { start: 3, end: 4 },
      { commits: [commit("aaa")], lineCommits: [0, 0], lineOriginals: [3, 4] },
    );

    // Line 3 may continue a run that starts above it; labelling it would move the label once line 2 loads.
    expect(labelsRun(partial, 3, filled)).toBe(false);
  });

  it("is false for a line with no attribution or past the end", () => {
    expect(labelsRun(runs, 6, filled)).toBe(false);
    expect(labelsRun(runs, 0, filled)).toBe(false);
  });
});

describe("fetching by range", () => {
  it("asks for nothing past the file's final newline", () => {
    const fresh = unloadedBlame(4, true);

    expect(missingRange(fresh, 1, 4)).toEqual({ start: 1, end: 3 });
    expect(missingRange(fresh, 4, 4)).toBeNull();
  });

  it("fills only the fetched lines, sharing commits the snapshot already has", () => {
    const first = mergeBlame(
      unloadedBlame(5, false),
      { start: 1, end: 2 },
      { commits: [commit("aaa")], lineCommits: [0, 0], lineOriginals: [1, 2] },
    );
    const second = mergeBlame(
      first,
      { start: 4, end: 5 },
      { commits: [commit("bbb"), commit("aaa")], lineCommits: [1, 0], lineOriginals: [4, 7] },
    );

    expect(shas(second)).toEqual(["aaa", "aaa", null, "aaa", "bbb"]);
    expect(second.commits.map((c) => c.sha)).toEqual(["aaa", "bbb"]);
    expect(blameAt(second, 5)?.originalLine).toBe(7);
    expect(missingRange(second, 1, 5)).toEqual({ start: 3, end: 3 });
  });

  it("lands a fetch on the lines it addressed even after edits moved them", () => {
    const requested = missingRange(unloadedBlame(3, false), 1, 3)!;
    // Two lines typed above the file's first line while the fetch was in flight.
    const edited = applyEdit(unloadedBlame(3, false), {
      startLine: 1,
      removedLines: 0,
      addedLines: 2,
      fromLineStart: true,
    });

    const merged = mergeBlame(edited, requested, {
      commits: [commit("aaa")],
      lineCommits: [0, 0, 0],
      lineOriginals: [1, 2, 3],
    });

    expect(shas(merged)).toEqual([null, null, "aaa", "aaa", "aaa"]);
    expect(missingRange(merged, 1, 5)).toBeNull();
  });

  it("stops a fetch where a deleted block left a gap in the disk lines", () => {
    // Lines 2-4 deleted unsaved: the buffer runs disk lines 1, 5, 6 — one fetch must not span what was deleted.
    const edited = applyEdit(unloadedBlame(6, false), {
      startLine: 2,
      removedLines: 3,
      addedLines: 0,
      fromLineStart: true,
    });

    expect(missingRange(edited, 1, 3)).toEqual({ start: 1, end: 1 });
  });

  it("keeps showing a line's last attribution while the file is fetched again", () => {
    const loaded = mergeBlame(
      unloadedBlame(2, false),
      { start: 1, end: 2 },
      { commits: [commit("aaa")], lineCommits: [0, 0], lineOriginals: [1, 2] },
    );

    const stale = refetchBlame(loaded, false);

    expect(shas(stale)).toEqual(["aaa", "aaa"]);
    expect(missingRange(stale, 1, 2)).toEqual({ start: 1, end: 2 });
    const fresh = mergeBlame(
      stale,
      { start: 1, end: 2 },
      { commits: [commit("bbb")], lineCommits: [0, 0], lineOriginals: [1, 2] },
    );
    expect(shas(fresh)).toEqual(["bbb", "bbb"]);
  });

  it("treats lines the response stops short of as past the file's end", () => {
    const merged = mergeBlame(
      unloadedBlame(3, false),
      { start: 1, end: 3 },
      { commits: [commit("aaa")], lineCommits: [0], lineOriginals: [1] },
    );

    expect(shas(merged)).toEqual(["aaa", null, null]);
    expect(missingRange(merged, 1, 3)).toBeNull();
  });
});

describe("blameAt", () => {
  it("has nothing for a line past the end of the blame", () => {
    expect(blameAt(snapshot(), 4)).toBeNull();
    expect(blameAt(snapshot(), 0)).toBeNull();
  });
});

describe("blameLabel", () => {
  const now = 1_700_000_000;

  it("names the author, when, and why", () => {
    expect(blameLabel(commit("aaa", { summary: "Fix the drain race" }), now + 3 * 86_400)).toBe(
      "Kapps, 3 days ago • Fix the drain race",
    );
  });

  it("says only that a working-tree line isn't committed", () => {
    expect(blameLabel(commit("0000", { uncommitted: true }), now)).toBe("Uncommitted changes");
  });

  it("truncates a subject that would run past the code it annotates", () => {
    const label = blameLabel(commit("aaa", { summary: "x".repeat(120) }), now);

    expect(label.endsWith("…")).toBe(true);
    expect(label.length).toBeLessThan(100);
  });
});

describe("relativeTime", () => {
  const now = 1_700_000_000;

  it.each([
    [30, "just now"],
    [90, "1 minute ago"],
    [3 * 3600, "3 hours ago"],
    [86_400, "1 day ago"],
    [3 * 86_400, "3 days ago"],
    [10 * 86_400, "1 week ago"],
    [60 * 86_400, "2 months ago"],
    [400 * 86_400, "1 year ago"],
  ])("reads %i seconds back as %s", (seconds, expected) => {
    expect(relativeTime(now - seconds, now)).toBe(expected);
  });

  it("never reads a clock-skewed future commit as negative", () => {
    expect(relativeTime(now + 5_000, now)).toBe("just now");
  });
});
