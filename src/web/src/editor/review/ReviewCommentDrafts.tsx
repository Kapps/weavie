import { createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { createReviewCommentComposer } from "./review-comment-composer";
import type { ReviewCommentDrafts as DraftStore } from "./review-comment-drafts";

/** Draft access does not depend on a file, diff body, or Monaco editor remaining mounted. */
export function ReviewCommentDrafts(props: { drafts: DraftStore }): JSX.Element {
  const [expanded, setExpanded] = createSignal(false);
  const drafts = () => props.drafts.retained();
  return (
    <Show when={drafts().length > 0}>
      <details
        class="weavie-review-drafts"
        onToggle={(event) => setExpanded(event.currentTarget.open)}
      >
        <summary>Comment drafts ({drafts().length})</summary>
        <Show when={expanded()}>
          <For each={drafts()}>
            {(draft) => {
              const composer = createReviewCommentComposer(props.drafts, draft);
              onCleanup(composer.dispose);
              return (
                <article class="weavie-pr-thread">
                  <div>
                    PR #{draft.file.number} · {draft.file.path} ·{" "}
                    {draft.target.kind === "new"
                      ? `Line ${draft.target.anchor.line}`
                      : `Thread ${draft.target.rootId}`}
                  </div>
                  {composer.element}
                </article>
              );
            }}
          </For>
        </Show>
      </details>
    </Show>
  );
}
