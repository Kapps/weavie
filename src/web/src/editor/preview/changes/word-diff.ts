import { computeDiffLines } from "../../review/diff-computation";

/** Character ranges of `after` that are new, and the old text removed at each position of `after`. */
export interface WordDiff {
  inserted: { start: number; end: number }[];
  deleted: { at: number; text: string }[];
}

const TOKEN = /\w+|\s+|[^\w\s]/gu;

/** Word-level changes from `before` to `after`, or null when the diff engine runs out of time. */
export function wordDiff(before: string, after: string): WordDiff | null {
  const was = before.match(TOKEN) ?? [];
  const now = after.match(TOKEN) ?? [];
  const changes = computeDiffLines(was, now);
  if (changes === null) return null;
  const offsets = [0];
  for (const token of now) offsets.push((offsets.at(-1) ?? 0) + token.length);
  const result: WordDiff = { inserted: [], deleted: [] };
  for (const { original, modified } of changes) {
    const at = offsets[modified.startLineNumber - 1] ?? after.length;
    if (!modified.isEmpty)
      result.inserted.push({
        start: at,
        end: offsets[modified.endLineNumberExclusive - 1] ?? after.length,
      });
    if (!original.isEmpty)
      result.deleted.push({
        at,
        text: was.slice(original.startLineNumber - 1, original.endLineNumberExclusive - 1).join(""),
      });
  }
  return result;
}
