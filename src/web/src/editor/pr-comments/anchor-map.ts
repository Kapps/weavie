// Maps PR comment lines (numbered against the PR head, or the merge-base for left-side comments) onto the live
// buffer, which may carry local edits or unpushed commits, and maps a buffer line back to the PR head.

import type { FixItem } from "../FixPrompt";
import { diffLines, type LineChanges, mapLine } from "../line-anchor";
import { splitDiffLines } from "../review/diff-computation";
import type { PrSources, PrThread } from "./pr-comments-store";

/** Where a PR thread sits in the buffer, and why it isn't on its literal line when it isn't. */
export interface ThreadPlacement {
  thread: PrThread;
  afterLine: number;
  /** The commented line's original text, shown when the buffer no longer has it at the anchor. */
  quote: string | null;
  note: "outdated" | "removed" | "changed" | null;
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
  const toBuffer = diffLines(head, buffer);
  let toHead: LineChanges | undefined;
  let baseToHead: LineChanges | undefined;
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
      baseToHead ??= diffLines(base, head);
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
      toHead ??= diffLines(buffer, head);
      const anchor = mapLine(toHead, bufferLine);
      return anchor.exact ? anchor.afterLine : null;
    },
  };
}

/** A thread as a Fix item: its buffer line and every comment, attributed; null for an outdated thread, which has
 * no live line to fix. */
export function threadFixItem(
  placement: ThreadPlacement,
  path: string,
  lineText: (line: number) => string,
): FixItem | null {
  if (placement.note === "outdated") return null;
  const line = Math.max(1, placement.afterLine);
  return {
    path,
    line,
    quote: placement.quote ?? lineText(line),
    text: placement.thread.comments
      .map((comment) => `${comment.author}: ${comment.body}`)
      .join("\n\n"),
  };
}
