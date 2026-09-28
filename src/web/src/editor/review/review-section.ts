import type { TextLocation } from "../nav-history";
import type { ReviewToolbarTarget } from "./review-toolbar-state";

/** Navigation and controls do not require a Monaco editor in each section. */
export interface ReviewSection {
  capture(): TextLocation;
  restore(location: TextLocation): void;
  revealFileStart(line: number): void;
  focus(): void;
  target(): ReviewToolbarTarget;
}
