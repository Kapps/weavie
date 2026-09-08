import type * as monaco from "monaco-editor";

type Editor = monaco.editor.IStandaloneCodeEditor;
type Installer = (editor: Editor) => () => void;
const editors = new Map<Editor, Map<Installer, () => void>>();
const installers = new Set<Installer>();
const commandTargets = new Set<monaco.editor.ICodeEditor>();
let active: monaco.editor.ICodeEditor | null = null;

/** The last explicitly focused editor remains the command target while menus and prompts have focus. */
export function activeEditor(): monaco.editor.ICodeEditor | null {
  return active;
}

/** Tracks command focus for document editors and Monaco's nested definition/reference editors. */
export function trackEditorFocus(editor: monaco.editor.ICodeEditor): void {
  commandTargets.add(editor);
  const focus = editor.onDidFocusEditorText(() => {
    active = editor;
  });
  editor.onDidDispose(() => {
    focus.dispose();
    if (active === editor) active = null;
    commandTargets.delete(editor);
  });
}

/** Registers Weavie's document editors for shared features, owned by the widget's lifetime. */
export function registerEditor(editor: Editor): void {
  const cleanups = new Map<Installer, () => void>();
  editors.set(editor, cleanups);
  for (const install of installers) cleanups.set(install, install(editor));
  editor.onDidDispose(() => {
    for (const cleanup of cleanups.values()) cleanup();
    editors.delete(editor);
  });
}

/** Installs shared behavior on every existing and future document editor, owned by its lifetime. */
export function observeEditors(install: Installer): () => void {
  installers.add(install);
  for (const [editor, cleanups] of editors) cleanups.set(install, install(editor));
  return () => {
    installers.delete(install);
    for (const cleanups of editors.values()) {
      cleanups.get(install)?.();
      cleanups.delete(install);
    }
  };
}

/** Resolves the actual right-clicked document editor before a context menu takes focus. */
export function editorAtElement(element: Element): monaco.editor.ICodeEditor | null {
  let target: monaco.editor.ICodeEditor | null = null;
  for (const editor of commandTargets) {
    const container = editor.getContainerDomNode();
    if (
      container.contains(element) &&
      (target === null || target.getContainerDomNode().contains(container))
    )
      target = editor;
  }
  return target;
}
