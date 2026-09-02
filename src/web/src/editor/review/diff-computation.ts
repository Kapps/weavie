import { linesDiffComputers } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/diff/linesDiffComputers";

export const DIFF_OPTIONS = {
  ignoreTrimWhitespace: false,
  maxComputationTimeMs: 1000,
  computeMoves: false,
} as const;

export const DIFF_ALGORITHM = "legacy" as const;

/** Split text into the same logical lines Monaco uses for its text models. */
export function splitDiffLines(text: string): string[] {
  return text.replace(/\r\n?/g, "\n").split("\n");
}

/** Compute detailed line mappings, or null when the diff engine reaches its time boundary. */
export function computeDiffLines(original: string[], modified: string[]) {
  // The legacy engine limits character refinement to small hunks, keeping complete large rewrites interactive.
  const result = linesDiffComputers.getLegacy().computeDiff(original, modified, DIFF_OPTIONS);
  return result.hitTimeout ? null : result.changes;
}

export type DiffLineChange = NonNullable<ReturnType<typeof computeDiffLines>>[number];

/** Compute detailed line mappings directly from text. The diff engine reports a visible timeout. */
export function computeTextDiffLines(original: string, modified: string) {
  return computeDiffLines(splitDiffLines(original), splitDiffLines(modified));
}
