// Pure line arithmetic behind in-place preview editing; block-editor.ts owns the DOM and the working copy.

/** Where `text` now sits in `lines`: `start` if unmoved, else its nearest copy; undefined when it's gone. */
export function relocate(lines: string[], text: string[], start: number): number | undefined {
  const at = (line: number): boolean => text.every((part, index) => lines[line + index] === part);
  for (let distance = 0; distance <= lines.length; distance += 1) {
    if (start - distance >= 0 && at(start - distance)) return start - distance;
    if (start + distance + text.length <= lines.length && at(start + distance))
      return start + distance;
  }
  return undefined;
}

/** The caret in `source` for a caret after `rendered`, skipping the markup that rendering removed. */
export function sourceOffset(source: string, rendered: string): number {
  let offset = source.match(LIST_MARKER)?.[0].length ?? source.match(/^#+\s+/)?.[0].length ?? 0;
  for (const char of rendered.trimStart()) {
    const next = source.indexOf(char, offset);
    if (next !== -1) offset = next + 1;
  }
  return offset;
}

const LIST_MARKER = /^[\s>]*(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?/;

/** The marker a new item after `line` starts with: same bullet, next number, unchecked box. */
export function nextListMarker(line: string): string {
  const marker = LIST_MARKER.exec(line)?.[0] ?? "- ";
  return marker.replace(/\d+/, (number) => String(Number(number) + 1)).replace(/\[[xX]\]/, "[ ]");
}

/** Whether `text` holds nothing but a list marker. */
export const isBareMarker = (text: string): boolean =>
  text.trim() !== "" && text.replace(LIST_MARKER, "").trim() === "";

/** The blank lines to insert at `at` so a new block can sit on its own `line` after the block ending at `after`. */
export function gapInsertion(
  lines: string[],
  after: number,
): { at: number; insert: string[]; line: number } {
  if (lines.length === 1 && lines[0] === "") return { at: 0, insert: [], line: 0 };
  if (after >= lines.length) return { at: lines.length, insert: ["", ""], line: lines.length + 1 };
  if (lines[after]?.trim() !== "") return { at: after, insert: ["", "", ""], line: after + 1 };
  const atEnd = after + 1 === lines.length;
  return { at: after + 1, insert: atEnd ? [""] : ["", ""], line: after + 1 };
}
