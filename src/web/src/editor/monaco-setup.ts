import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import tsWorker from "monaco-editor/esm/vs/language/typescript/ts.worker?worker";

// Wire Monaco's workers through Vite. Built as classic (iife) workers so they load
// reliably under the app:// custom scheme. If a worker fails to construct, Monaco still
// supports basic typing on the main thread — the latency path is unaffected.
self.MonacoEnvironment = {
  getWorker(_workerId: string, label: string): Worker {
    if (label === "typescript" || label === "javascript") {
      return new tsWorker();
    }
    return new editorWorker();
  },
};

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
    fontFamily: 'ui-monospace, "SF Mono", Menlo, monospace',
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
