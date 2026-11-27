import type * as monaco from "monaco-editor";
import type { AgentSelection } from "../agent/composer-state";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "./session-uri";

/** `range` of `model` as an attachment: its place as `path:line:column-line:column`, then its text. */
export function selectionExcerpt(
  model: monaco.editor.ITextModel,
  range: monaco.IRange,
): AgentSelection {
  const path = filePath(model) ?? model.uri.path;
  const place = `${range.startLineNumber}:${range.startColumn}-${range.endLineNumber}:${range.endColumn}`;
  return {
    label: `${path.split(/[\\/]/).at(-1)}:${place}`,
    text: `${path}:${place}\n${model.getValueInRange(range)}`,
  };
}

/** The last line `range` covers: one ending at a line's start covers the line above it. */
export const lastLineOf = (range: monaco.IRange): number =>
  range.endColumn === 1 && range.endLineNumber > range.startLineNumber
    ? range.endLineNumber - 1
    : range.endLineNumber;

// The path of the file `model` holds on its host, or null for a model that is not a file.
const filePath = (model: monaco.editor.ITextModel): string | null =>
  model.uri.scheme === SESSION_FILE_SCHEME ? sessionUriHostPath(model.uri) : null;

/** The path an anchor names for `model`: a file's path on its host, or any other model's URI. */
export const anchorPath = (model: monaco.editor.ITextModel): string =>
  filePath(model) ?? model.uri.toString();

/** The quote an anchor keeps of selected text: its first line with text, as long as an anchor allows. */
export const quoteOf = (text: string): string =>
  (text.split("\n").find((line) => line.trim() !== "") ?? text).trim().slice(0, 200);

/**
 * `range` cut to its last line with text, under which its card sits, and the quote of that line: what finds the
 * place again once the file has changed. Null when nothing in `range` has text.
 */
export function selectionQuote(
  model: monaco.editor.ITextModel,
  range: monaco.IRange,
): { range: monaco.IRange; quote: string } | null {
  for (let line = lastLineOf(range); line >= range.startLineNumber; line--) {
    const from = line === range.startLineNumber ? range.startColumn : 1;
    const to = line === range.endLineNumber ? range.endColumn : model.getLineMaxColumn(line);
    const quote = quoteOf(model.getLineContent(line).slice(from - 1, to - 1));
    if (quote !== "") return { range: { ...range, endLineNumber: line, endColumn: to }, quote };
  }
  return null;
}
