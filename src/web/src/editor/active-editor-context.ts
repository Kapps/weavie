import type * as monaco from "monaco-editor";
import { activeEditorMessage } from "./active-editor-message";
import { SESSION_FILE_SCHEME, sessionForUri } from "./session-uri";

/** One context owner across the main editor and mounted review sections. */
export function createActiveEditorContext(): {
  register(editor: monaco.editor.ICodeEditor): void;
  activate(editor: monaco.editor.ICodeEditor): void;
} {
  let active: monaco.editor.ICodeEditor | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const cancel = (): void => {
    clearTimeout(timer);
    timer = undefined;
  };
  const schedule = (): void => {
    cancel();
    timer = setTimeout(() => {
      timer = undefined;
      const model = active?.getModel();
      if (active === null || model == null || model.uri.scheme !== SESSION_FILE_SCHEME) return;
      const session = sessionForUri(model.uri);
      session
        ?.feature("editor")
        .publish("activeChanged", activeEditorMessage(model, active.getSelection()));
    }, 150);
  };
  const activate = (editor: monaco.editor.ICodeEditor): void => {
    active = editor;
    schedule();
  };
  return {
    activate,
    register: (editor) => {
      const changed = (): void => {
        if (active === editor) schedule();
      };
      const subscriptions = [
        editor.onDidFocusEditorText(() => activate(editor)),
        editor.onDidChangeCursorSelection(changed),
        editor.onDidChangeModel(changed),
        editor.onDidDispose(() => {
          if (active === editor) {
            cancel();
            active = null;
          }
          for (const subscription of subscriptions) subscription.dispose();
        }),
      ];
    },
  };
}
