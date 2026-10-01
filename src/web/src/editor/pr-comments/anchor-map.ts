// Maps PR comment lines (numbered against the PR head, or the merge-base for left-side comments) onto the live
// buffer, which may carry local edits or unpushed commits, and maps a buffer line back to the PR head.

import {
  computeTextDiffLines,
  type DiffLineChange,
  splitDiffLines,
} from "../review/diff-computation";
import type { PrSources, PrThread } from "./pr-comments-store";

interface LineAnchor {
  afterLine: number;
  exact: boolean;
}

/** Where a PR thread sits in the buffer, and why it isn't on its literal line when it isn't. */
export interface ThreadPlacement {
  thread: PrThread;
  afterLine: number;
  /** The commented line's original text, shown when the buffer no longer has it at the anchor. */
  quote: string | null;
  note: "outdated" | "removed" | "changed" | null;
}

type Changes = readonly DiffLineChange[];

// A line inside a changed range anchors after that range's replacement: the nearest surviving position.
function mapLine(changes: Changes, line: number): LineAnchor {
  let delta = 0;
  for (const change of changes) {
    if (line < change.original.startLineNumber) break;
    if (line < change.original.endLineNumberExclusive) {
      return { afterLine: change.modified.endLineNumberExclusive - 1, exact: false };
    }
    delta = change.modified.endLineNumberExclusive - change.original.endLineNumberExclusive;
  }
  return { afterLine: line + delta, exact: true };
}

function diff(original: string, modified: string): Changes {
  const changes = computeTextDiffLines(original, modified);
  if (changes === null) throw new Error("The file is too large to place PR comments on.");
  return changes;
}

/** Line mappings between one file's PR sources and the live buffer text. */
export interface AnchorMap {
  place(thread: PrThread): ThreadPlacement;
  /** The PR-head line a buffer line corresponds to, or null when the line only exists locally. */
  headLine(bufferLine: number): number | null;
}

export function createAnchorMap(sources: PrSources, buffer: string): AnchorMap {
  const head = sources.head ?? "";
  const headLines = splitDiffLines(head);
  const toBuffer = diff(head, buffer);
  let toHead: Changes | undefined;
  let baseToHead: Changes | undefined;
  return {
    place(thread) {
      if (thread.outdated) return { thread, afterLine: 0, quote: null, note: "outdated" };
      if (thread.side === "right") {
        const anchor = mapLine(toBuffer, thread.line);
        return anchor.exact
          ? { thread, afterLine: anchor.afterLine, quote: null, note: null }
          : {
              thread,
              afterLine: anchor.afterLine,
              quote: headLines[thread.line - 1] ?? null,
              note: "changed",
            };
      }
      const base = sources.base ?? "";
      baseToHead ??= diff(base, head);
      const inHead = mapLine(baseToHead, thread.line);
      const anchor = mapLine(toBuffer, inHead.afterLine);
      return {
        thread,
        afterLine: anchor.afterLine,
        quote: splitDiffLines(base)[thread.line - 1] ?? null,
        note: inHead.exact && anchor.exact ? null : inHead.exact ? "changed" : "removed",
      };
    },
    headLine(bufferLine) {
      toHead ??= diff(buffer, head);
      const anchor = mapLine(toHead, bufferLine);
      return anchor.exact ? anchor.afterLine : null;
    },
  };
}
