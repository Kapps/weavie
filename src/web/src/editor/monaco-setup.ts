import * as monaco from "monaco-editor";
import { currentFonts, onFontsChanged } from "../fonts";
import { registerActiveEditor } from "./vscode-services";

// Workers + the VSCode service substrate are wired in `vscode-services.ts` (initEditorServices),
// which must run before any editor is created. TypeScript/JS intelligence now comes from a real LSP
// server over the bridge (see lsp/lsp-client.ts), not Monaco's bundled ts.worker — so that worker is
// intentionally gone (spec §9: replace Monaco's in-browser TS immediately).

export function createEditor(container: HTMLElement): monaco.editor.IStandaloneCodeEditor {
  // The editor starts with NO document — an empty pane until the host opens a file (or, after a hot reload,
  // the editor host reattaches the file that was open before; see editor-host.ts). File models are created
  // on open with a real `file://` URI under the workspace: tsserver-family servers (tsgo) give loose
  // inmemory:/untitled: docs only partial service — they publish diagnostics for file:// docs, not in-memory
  // ones — so a file:// URI makes it a project file and squiggles flow.
  // Typography is a user setting resolved by the host (global font.* + editor.font.* overrides),
  // injected before navigation so we mount at the right font, and live-updated below.
  const font = currentFonts().editor;
  const editor = monaco.editor.create(container, {
    model: null,
    // No `theme` here on purpose: the active theme is global and owned by the theme controller (registered
    // + applied in vscode-services before any editor is created). Passing a `theme` option re-calls setTheme
    // and would clobber the active theme back to that value — which is exactly what left Monaco on vs-dark.
    fontSize: font.size,
    fontFamily: font.family,
    fontWeight: font.weight,
    automaticLayout: true,
    minimap: { enabled: true },
    bracketPairColorization: { enabled: true },
    smoothScrolling: false,
    cursorSmoothCaretAnimation: "off",
    renderWhitespace: "none",
    scrollBeyondLastLine: true,
  });

  // Apply live font changes (Monaco re-lays out on updateOptions); drop the subscription with the editor.
  const offFonts = onFontsChanged((config) =>
    editor.updateOptions({
      fontFamily: config.editor.family,
      fontSize: config.editor.size,
      fontWeight: config.editor.weight,
    }),
  );
  editor.onDidDispose(offFonts);

  // The editor service opens go-to-def / reveal-file targets through this editor (we own layout).
  registerActiveEditor(editor);
  return editor;
}

export { monaco };
