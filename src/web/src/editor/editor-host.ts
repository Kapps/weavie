// The Monaco editor and the @codingame/monaco-vscode-api service layer behind it are by far the
// heaviest code in the app. This module is *dynamically imported* (see App.tsx onMount) so all of it
// lands in a separate chunk that loads AFTER the shell (the terminal panes) has painted — keeping the
// multi-megabyte editor code off the first-paint path. Everything Monaco-touching that the shell needs
// is reached through the EditorHost handle returned here.

import { log } from "../bridge";
import { startLanguageServices } from "../lsp/lsp-client";
import { SAMPLE_CODE, createEditor, monaco } from "./monaco-setup";
import { initEditorServices } from "./vscode-services";

/** Resolves after two animation frames — enough for Monaco to lay out and paint its first frame. */
function nextPaint(): Promise<void> {
  return new Promise((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  });
}

/** The live editor, plus the operations the shell drives it with (open a file, reset to the sample). */
export interface EditorHost {
  readonly editor: monaco.editor.IStandaloneCodeEditor;
  /** Loads a file's contents into the editor and reveals <paramref>line</paramref> (host open-file). */
  openFile(path: string, content: string, line: number): void;
  /** Restores the sample document — used to reset the editor after a benchmark run. */
  resetSample(): void;
}

/**
 * Brings up the editor: initializes the VSCode services (which must precede any editor creation),
 * creates the editor in <paramref>container</paramref>, and wires lazy per-language LSP. The caller
 * (App) catches failures so a broken editor never takes down the terminal panes.
 */
export async function createEditorHost(container: HTMLElement): Promise<EditorHost> {
  await initEditorServices();
  const editor = createEditor(container);

  // Don't yank focus if the user has already clicked into a terminal while the editor was loading —
  // only claim focus when nothing else has it (matches the old eager-focus when the shell first mounts).
  if (document.activeElement === null || document.activeElement === document.body) {
    editor.focus();
  }

  // Lazy per-language LSP via the bridge (no-op if the host didn't inject bridge config); a client
  // connects the first time a document of its language is open.
  startLanguageServices();

  const openFile = (path: string, content: string, line: number): void => {
    const uri = monaco.Uri.file(path);
    const existing = monaco.editor.getModel(uri);
    const model = existing ?? monaco.editor.createModel(content, undefined, uri);
    if (existing) {
      existing.setValue(content);
    }
    editor.setModel(model);
    editor.revealLineInCenter(line);
    editor.setPosition({ lineNumber: line, column: 1 });
    editor.focus();
  };

  const resetSample = (): void => {
    editor.getModel()?.setValue(SAMPLE_CODE);
  };

  // Wait for Monaco's first real paint before resolving. The caller fades the splash on resolution, so
  // this keeps the editor's initial layout/paint hidden under the splash rather than flashing into view.
  await nextPaint();

  log("info", "editor host ready");
  return { editor, openFile, resetSample };
}
