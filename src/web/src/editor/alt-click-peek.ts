// Alt+Click on a symbol peeks its definition inline, dispatching the peek-definition command. Gated so
// Monaco's default alt-gestures survive wherever a peek can't happen: modifier combos, drags, non-word
// targets, and files with no definition provider all fall through to multicursor / column select.

import { getService, ILanguageFeaturesService } from "@codingame/monaco-vscode-api/services";
import { dispatchCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { monaco } from "./monaco-setup";

/** Installs the Alt+Click peek-definition gesture on `editor`; dispose to uninstall. */
export function installAltClickPeek(editor: monaco.editor.ICodeEditor): monaco.IDisposable {
  let pressed: monaco.IPosition | null = null;

  // Resolves immediately: editor-host awaits service init before any editor (and thus any click) exists.
  let features: ILanguageFeaturesService | undefined;
  void getService(ILanguageFeaturesService).then((service) => {
    features = service;
  });

  // The clicked text position, or null when the event isn't a plain Alt+left-click on a word.
  const wordPosition = (e: monaco.editor.IEditorMouseEvent): monaco.IPosition | null => {
    const { event, target } = e;
    if (!event.leftButton || !event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
      return null;
    }
    if (target.type !== monaco.editor.MouseTargetType.CONTENT_TEXT || target.position === null) {
      return null;
    }
    const model = editor.getModel();
    if (model === null || model.getWordAtPosition(target.position) === null) {
      return null;
    }
    return target.position;
  };

  const down = editor.onMouseDown((e) => {
    pressed = wordPosition(e);
  });
  const up = editor.onMouseUp((e) => {
    const from = pressed;
    pressed = null;
    const at = wordPosition(e);
    // Same position on press and release = a click; anything else is a drag and stays Monaco's.
    if (from === null || at === null || !monaco.Position.equals(from, at)) {
      return;
    }
    // No definition provider for this model (e.g. plain text) — leave the click to Monaco's multicursor.
    const model = editor.getModel();
    if (model === null || features === undefined || !features.definitionProvider.has(model)) {
      return;
    }
    editor.setPosition(at);
    void dispatchCommand(CommandIds.editorPeekDefinition);
  });

  return {
    dispose: () => {
      down.dispose();
      up.dispose();
    },
  };
}
