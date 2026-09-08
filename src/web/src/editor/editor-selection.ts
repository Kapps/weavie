import * as monaco from "monaco-editor";
import { noteSelectionChange, registerSelectionSource } from "../commands/selection";
import { activeEditorMessage } from "./active-editor-message";
import { activeEditor } from "./editor-instances";
import { setEditorStatus } from "./editor-status-store";
import { SESSION_FILE_SCHEME, sessionForUri } from "./session-uri-owner";

/** Publishes selection and status from the same document editor that receives commands. */
export function installEditorSelection(editor: monaco.editor.IStandaloneCodeEditor): () => void {
  // Tell the host which file + selection is active so embedded Claude knows what the user is looking at.
  // Debounced (cursor moves fire rapidly); the transient review model is suppressed — not a file being worked on.
  let emitTimer: ReturnType<typeof setTimeout> | undefined;
  const emitActiveEditor = (): void => {
    if (activeEditor() !== editor) return;
    const model = editor.getModel();
    if (model === null || model.uri.scheme !== SESSION_FILE_SCHEME) {
      return;
    }
    const session = sessionForUri(model.uri);
    if (session === undefined) {
      return;
    }
    const sel = editor.getSelection();
    session.feature("editor").publish("activeChanged", activeEditorMessage(model, sel));
  };
  const scheduleEmitActiveEditor = (): void => {
    if (emitTimer !== undefined) {
      clearTimeout(emitTimer);
    }
    emitTimer = setTimeout(emitActiveEditor, 150);
  };

  // Drive the editor status footer (cursor/selection/EOL). Written synchronously — the footer wants immediate
  // cursor feedback, unlike the debounced host emit above. Null when no real file model is showing.
  const updateStatus = (): void => {
    if (activeEditor() !== editor) return;
    const model = editor.getModel();
    const position = editor.getPosition();
    if (model === null || model.uri.scheme !== SESSION_FILE_SCHEME || position === null) {
      setEditorStatus(null);
      return;
    }
    let selectionCount = 0;
    for (const sel of editor.getSelections() ?? []) {
      selectionCount += model.getValueInRange(sel).length;
    }
    setEditorStatus({
      line: position.lineNumber,
      column: position.column,
      selectionCount,
      eol: model.getEndOfLineSequence() === monaco.editor.EndOfLineSequence.CRLF ? "CRLF" : "LF",
    });
  };

  const key = `editor:${editor.getId()}`;
  const offSelection = registerSelectionSource(key, () => {
    const model = editor.getModel();
    const selection = editor.getSelection();
    return activeEditor() !== editor || model === null || selection === null || selection.isEmpty()
      ? ""
      : model.getValueInRange(selection);
  });
  const changed = (): void => {
    if (activeEditor() !== editor) return;
    updateStatus();
    noteSelectionChange(key);
    scheduleEmitActiveEditor();
  };
  const subscriptions = [
    editor.onDidFocusEditorText(changed),
    editor.onDidChangeModel(changed),
    editor.onDidChangeCursorSelection(changed),
  ];
  changed();
  return () => {
    if (activeEditor() === null || activeEditor() === editor) setEditorStatus(null);
    clearTimeout(emitTimer);
    offSelection();
    for (const subscription of subscriptions) subscription.dispose();
  };
}
