import type * as monaco from "monaco-editor";
import { type Accessor, createSignal, onCleanup } from "solid-js";
import { DIFF_RECOMPUTE_DEBOUNCE_MS } from "./review/diff-zones";

/** A count that advances once `model`'s edits settle, so line placements recompute off the keystroke. */
export function settledContentVersion(model: monaco.editor.ITextModel): Accessor<number> {
  const [version, setVersion] = createSignal(0);
  let settle: ReturnType<typeof setTimeout> | undefined;
  const onContent = model.onDidChangeContent(() => {
    clearTimeout(settle);
    settle = setTimeout(() => setVersion((value) => value + 1), DIFF_RECOMPUTE_DEBOUNCE_MS);
  });
  onCleanup(() => {
    clearTimeout(settle);
    onContent.dispose();
  });
  return version;
}
