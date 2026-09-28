import { type Accessor, createEffect, createMemo, mapArray, onCleanup, untrack } from "solid-js";
import type { ReviewCopy } from "../editor-host";
import type { InlineDiffOptions } from "../inline-diff";
import type { ReviewDocumentScope } from "./review-document";
import { ReviewFileOwner } from "./review-file-owner";
import { hasReviewChanges, type ReviewFileDiff, type ReviewFileView } from "./review-store";

/** Configuration tracks file data independently of virtualized bodies and surface navigation. */
export function createReviewFileOwners(source: {
  files: Accessor<ReviewFileView[]>;
  label: Accessor<string>;
  documents: ReviewDocumentScope;
  optionsFor(diff: ReviewFileDiff): InlineDiffOptions;
  resolve(diff: ReviewFileDiff): Promise<ReviewCopy>;
}): Accessor<ReadonlyMap<ReviewFileView, ReviewFileOwner>> {
  const owners = createMemo(
    mapArray(source.files, (file) => {
      const diff = createMemo(file.diff);
      const comments = createMemo(file.comments);
      const input = createMemo(() => {
        const value = diff();
        comments();
        source.files();
        source.label();
        return value === null || !hasReviewChanges(value)
          ? undefined
          : {
              diff: value,
              options: untrack(() => source.optionsFor(value)),
            };
      });
      const owner = new ReviewFileOwner(source.documents, input, source.resolve);
      createEffect(() => {
        input();
        untrack(() => owner.refresh());
      });
      onCleanup(() => owner.dispose());
      return [file, owner] as const;
    }),
  );
  return createMemo(() => new Map(owners()));
}
