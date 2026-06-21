import { For, type JSX, Show } from "solid-js";

// Render `text` with its fuzzy-matched characters wrapped in <mark class="tb-hl">. `positions` are indices
// into the whole candidate string; `sliceStart` is where `text` begins within it, so a row's leaf and dir
// (both slices of the same path) highlight from one shared set of positions. Adjacent matches coalesce into a
// single <mark>.
export function highlightSlice(
  text: string,
  positions: Set<number> | undefined,
  sliceStart: number,
): JSX.Element {
  if (positions === undefined || positions.size === 0 || text.length === 0) {
    return text;
  }

  const segments: { text: string; hit: boolean }[] = [];
  let run = "";
  let runHit = false;
  for (let i = 0; i < text.length; i++) {
    const hit = positions.has(sliceStart + i);
    if (i === 0) {
      runHit = hit;
    } else if (hit !== runHit) {
      segments.push({ text: run, hit: runHit });
      run = "";
      runHit = hit;
    }
    run += text[i];
  }
  if (run.length > 0) {
    segments.push({ text: run, hit: runHit });
  }

  return (
    <For each={segments}>
      {(seg) => (
        <Show when={seg.hit} fallback={seg.text}>
          <mark class="tb-hl">{seg.text}</mark>
        </Show>
      )}
    </For>
  );
}
