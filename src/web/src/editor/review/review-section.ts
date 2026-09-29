import type { TextLocation } from "../nav-history";
import type { ReviewToolbarTarget } from "./review-toolbar-state";

export interface ReviewSectionInput {
  current(): boolean;
  focus(): void;
}

export interface ReviewSectionNavigation extends ReviewSectionInput {
  restore(location: TextLocation): void;
  revealFileStart(line: number): void;
}

export interface ReviewSectionFailure {
  error: unknown;
  retry(): void;
}

export type ReviewSectionState =
  | { kind: "collapsed" }
  | { kind: "empty" }
  | { kind: "pending"; input: ReviewSectionInput | undefined }
  | {
      kind: "unavailable";
      input: ReviewSectionInput | undefined;
      failure: ReviewSectionFailure;
      target: ReviewToolbarTarget;
    }
  | {
      kind: "ready";
      input: ReviewSectionInput | undefined;
      target: ReviewToolbarTarget;
      enter(intent: "focus" | "navigate"): ReviewSectionNavigation | undefined;
    };

/** Retained reading identity is independent of the current interaction capability. */
export interface ReviewSection {
  capture(): TextLocation;
  state(): ReviewSectionState;
}
