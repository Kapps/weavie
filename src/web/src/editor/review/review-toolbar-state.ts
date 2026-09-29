import type { InlineDiffActions, InlineDiffOptions, ParkedReview } from "../inline-diff";
import type { DiffMarkers } from "./diff-markers";
import type { ReviewDocument } from "./review-document";
import type { ReviewActionPresentation } from "./review-file-actions";

export type ReviewToolbarPaint =
  | { status: "pending" }
  | { status: "ready"; options: Readonly<InlineDiffOptions>; markers: DiffMarkers }
  | { status: "unavailable"; options: Readonly<InlineDiffOptions>; message: string };

export type ReviewToolbarTarget =
  | { kind: "none" }
  | { kind: "parked"; summary: ParkedReview }
  | {
      kind: "file";
      owner: object;
      document: ReviewDocument;
      presentation: ReviewActionPresentation;
      paint: ReviewToolbarPaint;
    };

export interface ReviewToolbarCommands extends InlineDiffActions {
  undoLast(): boolean;
}
