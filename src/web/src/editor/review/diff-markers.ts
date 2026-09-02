// The decoration + ghost-zone geometry of an inline diff, computed against a live model's lines. Shared by the
// file-review editor (inline-diff.ts) and the unified review's per-file editors, so both paint changes the same
// way. Pure: it touches no editor and owns no rendering.

import { reviewToModelLine } from "../diff-geometry";
import { monaco } from "../monaco-setup";
import { type DiffLineChange, splitDiffLines } from "./diff-computation";

/**
 * Coordinates + concurrency guard for reverting one hunk on disk. Ranges are 1-based, end-exclusive;
 * `guardText` is the current text the web sees — the host aborts if the file's current lines differ.
 */
export interface HunkRevert {
  baselineStart: number;
  baselineEndExclusive: number;
  currentStart: number;
  currentEndExclusive: number;
  guardText: string;
}

/**
 * Coordinates + concurrency guards for un-keeping one faded (accepted) hunk. `accepted*` is its range in the
 * accepted anchor (the lines spliced back); `review*` its range in the review baseline (the splice target).
 * Both sides are guarded with the text the web rendered — `guardText` (review baseline) and
 * `acceptedGuardText` (accepted anchor) — so the host aborts if either moved (a concurrent keep, or a turn
 * boundary committing the anchor) instead of splicing lines the user never saw.
 */
export interface HunkUnkeep {
  acceptedStart: number;
  acceptedEndExclusive: number;
  reviewStart: number;
  reviewEndExclusive: number;
  acceptedGuardText: string;
  guardText: string;
}

/** One bright (pending) change hunk: the line coordinates a keep/revert needs. `anchorLine` reveals it. */
export interface DiffHunk {
  anchorLine: number;
  baselineStart: number;
  baselineEndExclusive: number;
  currentStart: number;
  currentEndExclusive: number;
}

/** One faded (accepted) hunk: where it sits in the live model, plus the coordinates an un-keep needs. */
export interface AcceptedDiffHunk extends HunkUnkeep {
  anchorLine: number;
}

/** A removed-lines ghost rendered as a view zone under `afterLineNumber` (0 = above the first line). */
export interface GhostLines {
  afterLineNumber: number;
  lines: string[];
  faded: boolean;
}

/** The three stable text boundaries a live model's diff is painted from. */
export interface DiffMarkerTexts {
  /** The review baseline the live model is diffed against (the bright pending band). */
  original: string;
  /** The accepted anchor; its diff to `original` is the faded kept band. Undefined → no faded band. */
  acceptedBaseline: string | undefined;
  /** The content the agent produced; live lines that differ from it are the user's own typing. */
  claudeVersion: string | undefined;
}

/** Everything a surface needs to paint one file's diff over its live model. */
export interface DiffMarkers {
  decorations: monaco.editor.IModelDeltaDecoration[];
  ghosts: GhostLines[];
  hunks: DiffHunk[];
  acceptedHunks: AcceptedDiffHunk[];
  /** A wholly-new file (empty baseline): marked with one gutter edge + a header band, not washed line by line. */
  isNewFile: boolean;
}

/** Monaco-worker results needed to turn the three review boundaries into paint geometry. */
export interface DiffMarkerChanges {
  changes: DiffLineChange[];
  userChanges: DiffLineChange[];
  fadedChanges: DiffLineChange[];
}

const ADDED_RULER = {
  // Standard VS Code added-marker id so the ruler tracks the theme; the added/user shade distinction is
  // carried by the in-editor line wash, not the ruler.
  color: { id: "editorOverviewRuler.addedForeground" },
  position: monaco.editor.OverviewRulerLane.Left,
} as const;

/**
 * Builds paint geometry from Monaco-worker diff results. This work is linear and contains no diff computation.
 */
export function computeDiffMarkers(
  texts: DiffMarkerTexts,
  changes: DiffMarkerChanges,
): DiffMarkers {
  const original = splitDiffLines(texts.original);
  const isNewFile = texts.original.length === 0;
  const markers: DiffMarkers = {
    decorations: [],
    ghosts: [],
    hunks: [],
    acceptedHunks: [],
    isNewFile,
  };
  const acceptedBaseline =
    texts.acceptedBaseline === undefined || texts.acceptedBaseline === texts.original
      ? undefined
      : texts.acceptedBaseline;
  if (changes.changes.length === 0 && acceptedBaseline === undefined) {
    return markers; // no net change and nothing kept — nothing to paint
  }

  // Lines the user typed (diff the live model against `claudeVersion`) render fainter. Empty when
  // claudeVersion is omitted or the model still matches it.
  const userLines = new Set<number>();
  if (texts.claudeVersion !== undefined) {
    for (const change of changes.userChanges) {
      for (
        let ln = change.modified.startLineNumber;
        ln < change.modified.endLineNumberExclusive;
        ln++
      ) {
        userLines.add(ln);
      }
    }
  }

  const addLineDecoration = (
    startLine: number,
    endLineExclusive: number,
    className: string | null,
    gutterClassName: string,
  ): void => {
    markers.decorations.push({
      range: new monaco.Range(startLine, 1, endLineExclusive - 1, 1),
      options: {
        isWholeLine: true,
        className,
        linesDecorationsClassName: gutterClassName,
        overviewRuler: ADDED_RULER,
      },
    });
  };

  for (const change of changes.changes) {
    markers.hunks.push({
      anchorLine: Math.max(1, change.modified.startLineNumber),
      baselineStart: change.original.startLineNumber,
      baselineEndExclusive: change.original.endLineNumberExclusive,
      currentStart: change.modified.startLineNumber,
      currentEndExclusive: change.modified.endLineNumberExclusive,
    });
    if (!change.modified.isEmpty) {
      // One range covers every consecutive run with the same shade, bounding Monaco decoration allocations for
      // large rewrites while still splitting where user-authored and agent-authored lines meet.
      let segmentStart = change.modified.startLineNumber;
      let fromUser = !isNewFile && userLines.has(segmentStart);
      for (let line = segmentStart + 1; line < change.modified.endLineNumberExclusive; line++) {
        const nextFromUser = !isNewFile && userLines.has(line);
        if (nextFromUser !== fromUser) {
          addLineDecoration(
            segmentStart,
            line,
            fromUser ? "weavie-inline-user" : isNewFile ? null : "weavie-inline-added",
            fromUser ? "weavie-inline-user-gutter" : "weavie-inline-added-gutter",
          );
          segmentStart = line;
          fromUser = nextFromUser;
        }
      }
      addLineDecoration(
        segmentStart,
        change.modified.endLineNumberExclusive,
        fromUser ? "weavie-inline-user" : isNewFile ? null : "weavie-inline-added",
        fromUser ? "weavie-inline-user-gutter" : "weavie-inline-added-gutter",
      );
      if (!isNewFile) {
        for (const inner of change.innerChanges ?? []) {
          const r = inner.modifiedRange;
          // Char-level emphasis is for the agent's edits; skip it on the user's own faint lines.
          if (userLines.has(r.startLineNumber)) {
            continue;
          }
          const collapsed = r.startLineNumber === r.endLineNumber && r.startColumn === r.endColumn;
          if (!collapsed) {
            // className (not inlineClassName): an overlay div spanning the full line height, like VS Code's
            // char-insert — an inline span's background stops short of it, leaving a seam between lines.
            markers.decorations.push({
              range: r,
              options: { className: "weavie-inline-added-text", shouldFillLineOnLineBreak: true },
            });
          }
        }
      }
    }
    // A new file's "removed" side is only the empty baseline line — no ghost worth showing.
    if (!isNewFile && !change.original.isEmpty) {
      markers.ghosts.push({
        afterLineNumber: Math.max(0, change.modified.startLineNumber - 1),
        lines: original.slice(
          change.original.startLineNumber - 1,
          change.original.endLineNumberExclusive - 1,
        ),
        faded: false,
      });
    }
  }

  // The faded "accepted" band: kept-but-uncommitted hunks (acceptedBaseline → review baseline). They're EQUAL
  // between the review baseline (`original`) and the live model — a keep made them so — so they sit in the
  // UNCHANGED regions of the bright diff above. Translate each one's review-baseline position into a live model
  // line via that diff, wash it faded green in place, and record the coordinates an un-keep needs. The faded band
  // is a pure overlay: it never enters `hunks`, so navigation and Keep/Revert only touch bright pending hunks.
  if (acceptedBaseline !== undefined) {
    const accepted = splitDiffLines(acceptedBaseline);
    for (const change of changes.fadedChanges) {
      const reviewStart = change.modified.startLineNumber;
      const reviewEndExclusive = change.modified.endLineNumberExclusive;
      const modelStart = reviewToModelLine(changes.changes, reviewStart);
      if (reviewEndExclusive > reviewStart) {
        addLineDecoration(
          modelStart,
          modelStart + reviewEndExclusive - reviewStart,
          "weavie-inline-accepted",
          "weavie-inline-accepted-gutter",
        );
      }
      if (!change.original.isEmpty) {
        markers.ghosts.push({
          afterLineNumber: Math.max(0, modelStart - 1),
          lines: accepted.slice(
            change.original.startLineNumber - 1,
            change.original.endLineNumberExclusive - 1,
          ),
          faded: true,
        });
      }
      markers.acceptedHunks.push({
        anchorLine: Math.max(1, modelStart),
        acceptedStart: change.original.startLineNumber,
        acceptedEndExclusive: change.original.endLineNumberExclusive,
        reviewStart,
        reviewEndExclusive,
        acceptedGuardText: accepted
          .slice(change.original.startLineNumber - 1, change.original.endLineNumberExclusive - 1)
          .join("\n"),
        guardText: original.slice(reviewStart - 1, reviewEndExclusive - 1).join("\n"),
      });
    }
  }

  return markers;
}
