import * as monaco from "monaco-editor";
import { currentFonts, onFontsChanged } from "../fonts";
import { registerActiveEditor } from "./vscode-services";

// Workers + the VSCode service substrate are wired in `vscode-services.ts` (initEditorServices),
// which must run before any editor is created. TypeScript/JS intelligence now comes from a real LSP
// server over the bridge (see lsp/lsp-client.ts), not Monaco's bundled ts.worker — so that worker is
// intentionally gone (spec §9: replace Monaco's in-browser TS immediately).

export const SAMPLE_CODE = `// weavie — Monaco typing-latency gate.
// Type freely; the HUD up top shows keydown->frame and input->paint percentiles.
// Toggle "load" to feel the under-load tail (simulated terminal firehose).

interface Node<T> {
  value: T;
  next: Node<T> | null;
}

function* walk<T>(head: Node<T> | null): Generator<T> {
  for (let n = head; n !== null; n = n.next) {
    yield n.value;
  }
}

const build = (xs: readonly number[]): Node<number> | null =>
  xs.reduceRight<Node<number> | null>((next, value) => ({ value, next }), null);

const list = build([1, 1, 2, 3, 5, 8, 13, 21, 34, 55]);
console.log([...walk(list)].reduce((a, b) => a + b, 0));
`;

// The scratch/sample document's URI (a non-existent file:// under the workspace). Exported so the
// editor host can suppress it from active-editor reports — it isn't something the user is "working on".
export const SCRATCH_URI: monaco.Uri | undefined = (() => {
  const workspace = window.__WEAVIE_LSP__?.workspace;
  return workspace !== undefined
    ? monaco.Uri.file(`${workspace.replaceAll("\\", "/")}/weavie-scratch.ts`)
    : undefined;
})();

export function createEditor(container: HTMLElement): monaco.editor.IStandaloneCodeEditor {
  // Back the editor with a real file:// model URI under the workspace. tsserver-family servers (tsgo)
  // give loose inmemory:/untitled: docs only partial service — they publish diagnostics for file://
  // docs, not for in-memory ones — so a file:// URI makes it a project file and squiggles flow.
  // Reuse an existing scratch model: a hot reload disposes the prior editor (App's onCleanup) but Monaco
  // doesn't dispose a model it was handed, so re-creating SCRATCH_URI would throw "model already exists".
  const existing = SCRATCH_URI !== undefined ? monaco.editor.getModel(SCRATCH_URI) : null;
  const model = existing ?? monaco.editor.createModel(SAMPLE_CODE, "typescript", SCRATCH_URI);
  // Typography is a user setting resolved by the host (global font.* + editor.font.* overrides),
  // injected before navigation so we mount at the right font, and live-updated below.
  const font = currentFonts().editor;
  const editor = monaco.editor.create(container, {
    model,
    theme: "vs-dark",
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
