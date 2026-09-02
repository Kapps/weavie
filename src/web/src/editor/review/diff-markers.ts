// The decoration + ghost-zone geometry of an inline diff, computed against a live model's lines. Shared by the
// file-review editor (inline-diff.ts) and the unified review's per-file editors, so both paint changes the same
// way. Pure: it touches no editor and owns no rendering.

import { reviewToModelLine } from "../diff-geometry";
import { monaco } from "../monaco-setup";
import { computeDiffLines, splitDiffLines } from "./diff-computation";

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

/** The three text boundaries a diff is painted from. `modified` is always the live model's lines. */
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

const ADDED_RULER = {
  // Standard VS Code added-marker id so the ruler tracks the theme; the added/user shade distinction is
  // carried by the in-editor line wash, not the ruler.
  color: { id: "editorOverviewRuler.addedForeground" },
  position: monaco.editor.OverviewRulerLane.Left,
} as const;

/**
 * Computes the diff geometry of `modified` (the live model's lines) against the baselines in `texts`.
 * Returns null when the diff engine times out, so the caller can surface that instead of painting a partial diff.
 */
export function computeDiffMarkers(texts: DiffMarkerTexts, modified: string[]): DiffMarkers | null {
  const original = splitDiffLines(texts.original);
  const changes = computeDiffLines(original, modified);
  if (changes === null) {
    return null;
  }
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
  if (changes.length === 0 && acceptedBaseline === undefined) {
    return markers; // no net change and nothing kept — nothing to paint
  }

  // Lines the user typed (diff the live model against `claudeVersion`) render fainter. Empty when
  // claudeVersion is omitted or the model still matches it.
  const userLines = new Set<number>();
  if (texts.claudeVersion !== undefined) {
    const userDiff = computeDiffLines(splitDiffLines(texts.claudeVersion), modified);
    if (userDiff === null) {
      return null;
    }
    for (const change of userDiff) {
      for (
        let ln = change.modified.startLineNumber;
        ln < change.modified.endLineNumberExclusive;
        ln++
      ) {
        userLines.add(ln);
      }
    }
  }

  for (const change of changes) {
    markers.hunks.push({
      anchorLine: Math.max(1, change.modified.startLineNumber),
      baselineStart: change.original.startLineNumber,
      baselineEndExclusive: change.original.endLineNumberExclusive,
      currentStart: change.modified.startLineNumber,
      currentEndExclusive: change.modified.endLineNumberExclusive,
    });
    if (!change.modified.isEmpty) {
      // Per-line so a block mixing the agent's lines with the user's tweaks paints each in its own shade. A new
      // file skips the wash + char overlay (isNewFile) — only the continuous gutter edge marks it.
      for (
        let ln = change.modified.startLineNumber;
        ln < change.modified.endLineNumberExclusive;
        ln++
      ) {
        const fromUser = userLines.has(ln);
        markers.decorations.push({
          range: new monaco.Range(ln, 1, ln, 1),
          options: {
            isWholeLine: true,
            className: isNewFile ? null : fromUser ? "weavie-inline-user" : "weavie-inline-added",
            linesDecorationsClassName: isNewFile
              ? "weavie-inline-added-gutter"
              : fromUser
                ? "weavie-inline-user-gutter"
                : "weavie-inline-added-gutter",
            overviewRuler: ADDED_RULER,
          },
        });
      }
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
    const fadedChanges = computeDiffLines(accepted, original);
    if (fadedChanges === null) {
      return null;
    }
    for (const change of fadedChanges) {
      const reviewStart = change.modified.startLineNumber;
      const reviewEndExclusive = change.modified.endLineNumberExclusive;
      const modelStart = reviewToModelLine(changes, reviewStart);
      for (let i = 0; i < reviewEndExclusive - reviewStart; i++) {
        markers.decorations.push({
          range: new monaco.Range(modelStart + i, 1, modelStart + i, 1),
          options: {
            isWholeLine: true,
            className: "weavie-inline-accepted",
            linesDecorationsClassName: "weavie-inline-accepted-gutter",
            overviewRuler: ADDED_RULER,
          },
        });
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
