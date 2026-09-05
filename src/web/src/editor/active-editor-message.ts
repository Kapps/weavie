import type * as monaco from "monaco-editor";
import { sessionUriHostPath } from "./session-uri";

export function activeEditorMessage(
  model: monaco.editor.ITextModel,
  selection: monaco.Selection | null,
): object {
  const text = selection !== null && !selection.isEmpty() ? model.getValueInRange(selection) : "";
  return {
    path: sessionUriHostPath(model.uri),
    languageId: model.getLanguageId(),
    text,
    selection: {
      start: {
        line: (selection?.startLineNumber ?? 1) - 1,
        character: (selection?.startColumn ?? 1) - 1,
      },
      end: {
        line: (selection?.endLineNumber ?? 1) - 1,
        character: (selection?.endColumn ?? 1) - 1,
      },
      isEmpty: text.length === 0,
    },
  };
}
