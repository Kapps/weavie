import type * as monaco from "monaco-editor";
import type { AgentSelection } from "../agent/composer-state";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "./session-uri";

/** `range` of `model` as an attachment: its place as `path:line:column-line:column`, then its text. */
export function selectionExcerpt(
  model: monaco.editor.ITextModel,
  range: monaco.IRange,
): AgentSelection {
  const path =
    model.uri.scheme === SESSION_FILE_SCHEME ? sessionUriHostPath(model.uri) : model.uri.path;
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
