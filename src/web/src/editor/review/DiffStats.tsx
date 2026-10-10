import type { JSX } from "solid-js";

/** A diff's added/removed line counts. */
export function DiffStats(props: { added: number; removed: number }): JSX.Element {
  return (
    <span class="unified-review-file-stats">
      <span class="unified-review-added">+{props.added}</span>
      <span class="unified-review-removed">−{props.removed}</span>
    </span>
  );
}
