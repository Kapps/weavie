import { type JSX, Show } from "solid-js";

/** A file's reviewed check; the empty slot keeps the counts beside it aligned. */
export function ReviewedMark(props: { reviewed: boolean }): JSX.Element {
  return (
    <Show when={props.reviewed} fallback={<span class="unified-review-reviewed" />}>
      <span class="unified-review-reviewed" role="img" aria-label="Reviewed">
        ✓
      </span>
    </Show>
  );
}
