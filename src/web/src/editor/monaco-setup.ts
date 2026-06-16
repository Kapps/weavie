import * as monaco from "monaco-editor";

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

export function createEditor(container: HTMLElement): monaco.editor.IStandaloneCodeEditor {
  return monaco.editor.create(container, {
    value: SAMPLE_CODE,
    language: "typescript",
    theme: "vs-dark",
    fontSize: 14,
    // Cross-platform monospace stack (spec §4 fix): the old macOS-only list silently fell back to
    // generic monospace on Windows. Typography becomes a real setting later (owned by the settings agent).
    fontFamily:
      'ui-monospace, "Cascadia Code", "SF Mono", Menlo, Consolas, "Courier New", monospace',
    automaticLayout: true,
    minimap: { enabled: true },
    bracketPairColorization: { enabled: true },
    smoothScrolling: false,
    cursorSmoothCaretAnimation: "off",
    renderWhitespace: "none",
    scrollBeyondLastLine: true,
  });
}

export { monaco };
