import { linesDiffComputers } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/diff/linesDiffComputers";

const DIFF_OPTIONS = {
  ignoreTrimWhitespace: false,
  maxComputationTimeMs: 1000,
  computeMoves: false,
} as const;

export const MAX_DIFF_LINES = 2_000;
export const MAX_DIFF_CHARACTERS = 500_000;

/** Split text into the same logical lines Monaco uses for its text models. */
export function splitDiffLines(text: string): string[] {
  return text.replace(/\r\n?/g, "\n").split("\n");
}

/** Whether rendering a line diff would exceed the explicit, user-visible per-file boundary. */
export function diffTextTooLarge(text: string): boolean {
  if (text.length > MAX_DIFF_CHARACTERS) {
    return true;
  }
  let lines = 1;
  for (let i = 0; i < text.length; i++) {
    const char = text.charCodeAt(i);
    if (
      (char === 10 || (char === 13 && text.charCodeAt(i + 1) !== 10)) &&
      ++lines > MAX_DIFF_LINES
    ) {
      return true;
    }
  }
  return false;
}

/** Compute detailed line mappings, or null when the explicit size/time boundary is reached. */
export function computeDiffLines(original: string[], modified: string[]) {
  const result = linesDiffComputers.getDefault().computeDiff(original, modified, DIFF_OPTIONS);
  return result.hitTimeout ? null : result.changes;
}

export type DiffLineChange = NonNullable<ReturnType<typeof computeDiffLines>>[number];

/** Compute detailed line mappings directly from text. The diff engine reports a visible timeout. */
export function computeTextDiffLines(original: string, modified: string) {
  return computeDiffLines(splitDiffLines(original), splitDiffLines(modified));
}
