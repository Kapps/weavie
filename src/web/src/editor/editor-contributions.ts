import type * as monaco from "monaco-editor";
import { type ClientSession, selectedSession } from "../bridge";
import { noteSelectionChange, registerSelectionSource } from "../commands/selection";
import { createSymbolSource } from "../symbols/symbol-source";
import { activeEditorMessage } from "./active-editor-message";
import { installAltClickPeek } from "./alt-click-peek";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import { setEditorStatus } from "./editor-status-store";
import { createGitBlame } from "./git-blame";
import type { NavLocation } from "./nav-history";
import { sharedReviseMarks } from "./revise-marks";
import { SESSION_FILE_SCHEME } from "./session-uri-owner";
import { createSpellCheck } from "./spell-check";

/** Shared text behavior is installed once per exact editor/model binding, including virtualized sections. */
export function connectTextEditor(options: {
  session: ClientSession;
  kind: "file" | "review";
  editor: monaco.editor.IStandaloneCodeEditor;
  model: monaco.editor.ITextModel;
  capture(): NavLocation | undefined;
  restore(location: NavLocation): void;
}): { connection: TextEditorConnection; dispose(): void } {
  const { editor, model, session } = options;
  const lifetime = new AbortController();
  const connection: TextEditorConnection = {
    ...options,
    signal: lifetime.signal,
    symbols: createSymbolSource(session, model, lifetime.signal),
    blame: createGitBlame(editor),
    spelling: createSpellCheck(editor),
  };
  const unregister = editorContexts.register(connection);
  const offRevise = sharedReviseMarks.attach(connection);
  const key = `editor:${editor.getId()}`;
  const offSelection = registerSelectionSource(key, () => {
    if (editorContexts.get(session) !== connection) return "";
    const selection = editor.getSelection();
    return selection === null ? "" : model.getValueInRange(selection);
  });
  const publish = (): void => {
    if (editorContexts.get(session) !== connection) return;
    const selection = editor.getSelection();
    if (model.uri.scheme === SESSION_FILE_SCHEME) {
      session.feature("editor").publish("activeChanged", activeEditorMessage(model, selection));
    }
    const position = editor.getPosition();
    if (selectedSession() === session && position !== null) {
      setEditorStatus({
        line: position.lineNumber,
        column: position.column,
        selectionCount: (editor.getSelections() ?? []).reduce(
          (count, range) => count + model.getValueInRange(range).length,
          0,
        ),
        eol: model.getEOL() === "\r\n" ? "CRLF" : "LF",
      });
    }
    noteSelectionChange(key);
    editorContexts.changed(connection);
  };
  const subscriptions = [
    editor.onDidFocusEditorText(() => {
      editorContexts.activate(connection);
      publish();
    }),
    editor.onDidChangeCursorSelection(publish),
    installAltClickPeek(editor),
  ];
  publish();
  return {
    connection,
    dispose: () => {
      if (editorContexts.isCurrent(connection) && selectedSession() === session)
        setEditorStatus(null);
      lifetime.abort();
      unregister();
      offRevise();
      offSelection();
      for (const subscription of subscriptions) subscription.dispose();
      connection.blame.dispose();
      connection.spelling.dispose();
    },
  };
}
