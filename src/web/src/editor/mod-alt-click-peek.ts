import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { ILanguageFeaturesService } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/services/languageFeatures.service";
import { IS_MAC } from "../commands/keybindings";
import { dispatchCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { monaco } from "./monaco-setup";

/** Installs the Mod+Alt+Click peek-definition gesture; dispose to uninstall. */
export function installModAltClickPeek(editor: monaco.editor.ICodeEditor): monaco.IDisposable {
  const dom = editor.getContainerDomNode();
  let pressed: monaco.IPosition | null = null;

  const wordPosition = (event: MouseEvent): monaco.IPosition | null => {
    const mod = IS_MAC ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey;
    if (event.button !== 0 || !mod || !event.altKey || event.shiftKey) {
      return null;
    }
    const target = editor.getTargetAtClientPoint(event.clientX, event.clientY);
    if (target?.type !== monaco.editor.MouseTargetType.CONTENT_TEXT || target.position === null) {
      return null;
    }
    const model = editor.getModel();
    if (model === null || model.getWordAtPosition(target.position) === null) {
      return null;
    }
    return target.position;
  };

  const down = editor.onMouseDown((e) => {
    pressed = wordPosition(e.event.browserEvent);
  });
  const move = (event: PointerEvent): void => {
    if (
      pressed !== null &&
      !monaco.Position.equals(
        pressed,
        editor.getTargetAtClientPoint(event.clientX, event.clientY)?.position ?? null,
      )
    ) {
      pressed = null;
    }
  };
  const up = (event: MouseEvent): void => {
    const from = pressed;
    pressed = null;
    const at = wordPosition(event);
    if (from === null || at === null || !monaco.Position.equals(from, at)) {
      return;
    }
    const model = editor.getModel();
    if (
      model === null ||
      !StandaloneServices.get(ILanguageFeaturesService).definitionProvider.has(model)
    ) {
      return;
    }
    // Capture before Monaco handles Mod+Alt+Click as go-to-definition in a side editor.
    event.preventDefault();
    event.stopPropagation();
    editor.setPosition(at);
    void dispatchCommand(CommandIds.editorPeekDefinition);
  };
  dom.addEventListener("pointermove", move, { capture: true });
  dom.addEventListener("mouseup", up, { capture: true });

  return {
    dispose: () => {
      dom.removeEventListener("mouseup", up, { capture: true });
      down.dispose();
      dom.removeEventListener("pointermove", move, { capture: true });
    },
  };
}
