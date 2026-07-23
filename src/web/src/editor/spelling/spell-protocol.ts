import type { EditorBinding, SpellProjection } from "../../bridge";

interface ContentChange {
  range: { startLineNumber: number; endLineNumber: number };
  text: string;
}

/** Returns only the post-edit lines directly changed by Monaco's end-to-start change batch. */
export function changedLineNumbers(changes: readonly ContentChange[], lineCount: number): number[] {
  if (lineCount < 1) {
    return [];
  }

  const ranges: Array<readonly [number, number]> = [];
  let lineOffset = 0;
  for (let index = changes.length - 1; index >= 0; index -= 1) {
    const change = changes[index]!;
    const start = change.range.startLineNumber + lineOffset;
    const insertedLines = lineBreaks(change.text);
    ranges[index] = [start, start + insertedLines];
    lineOffset += insertedLines - (change.range.endLineNumber - change.range.startLineNumber);
  }

  const lines = new Set<number>();
  for (const [index] of changes.entries()) {
    const [rawStart, rawEnd] = ranges[index]!;
    const start = Math.min(lineCount, Math.max(1, rawStart));
    const end = Math.min(lineCount, Math.max(start, rawEnd));
    for (let line = start; line <= end; line += 1) {
      lines.add(line);
    }
  }
  return [...lines];
}

/** A spelling reply may affect the visible editor only when every immutable projection field still agrees. */
export function ownsSpellProjection(
  binding: EditorBinding | null,
  message: SpellProjection,
): boolean {
  return (
    binding !== null &&
    binding.protocol === "projection" &&
    binding.sessionId === message.sessionId &&
    binding.projectionEpoch === message.projectionEpoch &&
    binding.projectionRevision === message.projectionRevision &&
    binding.projectionPageId === message.projectionPageId
  );
}

/** Extracts the direct word accepted by dictionary commands invoked outside an editor menu. */
export function directSpellWord(args: unknown): string | null {
  if (typeof args !== "object" || args === null) {
    return null;
  }
  const word = (args as Record<string, unknown>).word;
  return typeof word === "string" && word.length > 0 ? word : null;
}

function lineBreaks(text: string): number {
  let count = 0;
  for (const character of text) {
    if (character === "\n") {
      count += 1;
    }
  }
  return count;
}
