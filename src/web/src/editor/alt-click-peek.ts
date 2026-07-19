// Alt+Click on a symbol peeks its definition inline, dispatching the peek-definition command. Gated so
// Monaco's default alt-gestures survive wherever a peek can't happen or the user is visibly mid-gesture:
// modifier combos, drags, non-word targets, files with no definition provider, and an in-progress
// multicursor session all fall through to multicursor / column select. Accepted cost: alt+click on a word
// in a definition-backed file no longer ADDS a cursor — Ctrl+D, non-word targets, and an existing
// multicursor session keep that gesture.

import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { ILanguageFeaturesService } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/services/languageFeatures.service";
import { dispatchCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { monaco } from "./monaco-setup";

/** Installs the Alt+Click peek-definition gesture on `editor`; dispose to uninstall. */
export function installAltClickPeek(editor: monaco.editor.ICodeEditor): monaco.IDisposable {
  // The container, not getDomNode(): the latter is null until a model attaches, the container always exists.
  const dom = editor.getContainerDomNode();
  let pressed: monaco.IPosition | null = null;

  // Monaco adds the alt+click cursor BEFORE emitting onMouseDown, so the pre-click selection count — the
  // "already mid-multicursor?" signal — is sampled by a capture-phase DOM listener that runs ahead of it.
  let selectionsBeforeClick = 1;
  const sample = (): void => {
    selectionsBeforeClick = editor.getSelections()?.length ?? 1;
  };
  dom.addEventListener("mousedown", sample, { capture: true });

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
    pressed = selectionsBeforeClick > 1 ? null : wordPosition(e);
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
    if (
      model === null ||
      !StandaloneServices.get(ILanguageFeaturesService).definitionProvider.has(model)
    ) {
      return;
    }
    editor.setPosition(at);
    void dispatchCommand(CommandIds.editorPeekDefinition);
  });

  return {
    dispose: () => {
      dom.removeEventListener("mousedown", sample, { capture: true });
      down.dispose();
      up.dispose();
    },
  };
}
