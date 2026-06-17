// The Monaco editor and the @codingame/monaco-vscode-api service layer behind it are by far the
// heaviest code in the app. This module is *dynamically imported* (see App.tsx onMount) so all of it
// lands in a separate chunk that loads AFTER the shell (the terminal panes) has painted — keeping the
// multi-megabyte editor code off the first-paint path. Everything Monaco-touching that the shell needs
// is reached through the EditorHost handle returned here.

import { log, postToHost } from "../bridge";
import { startLanguageServices } from "../lsp/lsp-client";
import { SAMPLE_CODE, SCRATCH_URI, createEditor, monaco } from "./monaco-setup";
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
  /**
   * Refreshes an already-open file's contents in place after an edit was accepted elsewhere (the diff
   * Keep), preserving the scroll/cursor view state and never stealing focus. No-op when the file isn't
   * open — the editor isn't showing it, so there's nothing to refresh.
   */
  applyExternalEdit(path: string, content: string): void;
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

  // Tell the host which file + selection is active so the embedded Claude always knows what the user
  // is looking at (the host pushes a selection_changed notification + answers getCurrentSelection from
  // it). Debounced — cursor moves fire rapidly and Claude only needs the settled state. The scratch
  // sample doc is suppressed; it isn't a file the user is working on.
  let emitTimer: ReturnType<typeof setTimeout> | undefined;
  const emitActiveEditor = (): void => {
    const model = editor.getModel();
    if (
      model === null ||
      model.uri.scheme !== "file" ||
      model.uri.toString() === SCRATCH_URI?.toString()
    ) {
      return;
    }
    const sel = editor.getSelection();
    const text = sel !== null && !sel.isEmpty() ? model.getValueInRange(sel) : "";
    // Monaco positions are 1-based; the IDE selection protocol is 0-based.
    postToHost({
      type: "active-editor-changed",
      uri: model.uri.toString(),
      languageId: model.getLanguageId(),
      text,
      selection: {
        start: {
          line: (sel?.startLineNumber ?? 1) - 1,
          character: (sel?.startColumn ?? 1) - 1,
        },
        end: { line: (sel?.endLineNumber ?? 1) - 1, character: (sel?.endColumn ?? 1) - 1 },
        isEmpty: text.length === 0,
      },
    });
  };
  const scheduleEmitActiveEditor = (): void => {
    if (emitTimer !== undefined) {
      clearTimeout(emitTimer);
    }
    emitTimer = setTimeout(emitActiveEditor, 150);
  };
  editor.onDidChangeModel(scheduleEmitActiveEditor);
  editor.onDidChangeCursorSelection(scheduleEmitActiveEditor);

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

  const applyExternalEdit = (path: string, content: string): void => {
    const model = monaco.editor.getModel(monaco.Uri.file(path));
    if (model === null || model.getValue() === content) {
      return;
    }
    // Restore the view state around setValue so an accepted edit doesn't fling the visible file back to
    // line 1; only meaningful when this is the editor's active model.
    const isActive = editor.getModel() === model;
    const viewState = isActive ? editor.saveViewState() : null;
    model.setValue(content);
    if (viewState !== null) {
      editor.restoreViewState(viewState);
    }
  };

  const resetSample = (): void => {
    editor.getModel()?.setValue(SAMPLE_CODE);
  };

  // Wait for Monaco's first real paint before resolving. The caller fades the splash on resolution, so
  // this keeps the editor's initial layout/paint hidden under the splash rather than flashing into view.
  await nextPaint();

  log("info", "editor host ready");
  return { editor, openFile, applyExternalEdit, resetSample };
}
