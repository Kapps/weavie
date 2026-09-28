import type { Accessor } from "solid-js";
import type { ReviewCopy } from "../editor-host";
import type { ReviewDocument } from "./review-document";
import type { PassiveDocument } from "./review-passive-document";
import type { renderPassiveChunks } from "./review-passive-lines";
import type { captureReviewDecorations } from "./review-projection-decorations";
import type { ReviewFileDiff } from "./review-store";

export interface PreparedPassiveReview {
  copy: ReviewCopy;
  document: ReviewDocument;
  source: PassiveDocument;
  diff: ReviewFileDiff;
  rendered: ReturnType<typeof renderPassiveChunks>;
  width: number;
  decorations: ReturnType<typeof captureReviewDecorations>;
}

export interface PassiveReviewPresentation {
  prepared: Accessor<PreparedPassiveReview | undefined>;
  current(): PreparedPassiveReview | undefined;
  displayed(): PreparedPassiveReview | undefined;
  document(): ReviewDocument | undefined;
  error: Accessor<string>;
  bounds(): { top: number; bottom: number; height: number };
  reveal(top: number): void;
  suspend(): void;
  resume(): void;
  layout(): void;
  shift(delta: number): void;
}
