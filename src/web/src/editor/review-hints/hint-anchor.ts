import { diffLines, type LineChanges, mapLine } from "../line-anchor";
import type { ReviewHint } from "./review-hints-store";

/** A hint on its live-buffer line; `changed` once that line was edited since the hint was captured. */
export interface PlacedHint {
  hint: ReviewHint;
  line: number;
  changed: boolean;
}

/** Places each hint by diffing the file text it was captured against with the live buffer. */
export function placeHints(
  hints: readonly ReviewHint[],
  sources: ReadonlyMap<string, string>,
  buffer: string,
): PlacedHint[] {
  const maps = new Map<string, LineChanges>();
  return hints.map((hint) => {
    const source = sources.get(hint.sourceHash);
    if (source === undefined) throw new Error(`Review hint ${hint.id} has no captured source.`);
    const changes = maps.get(hint.sourceHash) ?? diffLines(source, buffer);
    maps.set(hint.sourceHash, changes);
    const anchor = mapLine(changes, hint.line);
    return { hint, line: Math.max(1, anchor.afterLine), changed: !anchor.exact };
  });
}
