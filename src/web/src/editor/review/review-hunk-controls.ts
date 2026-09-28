import { CommandIds } from "../../commands/types";
import { reviewHunkLine } from "../diff-geometry";
import type { DiffMarkers } from "./diff-markers";
import type { ReviewDocument } from "./review-document";
import type { ReviewActionPresentation } from "./review-file-actions";
import { makeButton, withShortcut } from "./review-toolbar";

/** Both paint adapters use the same guarded hunk actions and discoverable shortcuts. */
export function buildReviewHunkControls(
  documentModel: ReviewDocument,
  presentation: ReviewActionPresentation,
  markers: DiffMarkers,
  track: (button: HTMLButtonElement) => HTMLButtonElement,
) {
  const controls: { id: string; line: number; element: HTMLElement }[] = [];
  const options = documentModel.actions.options;
  if (options?.mode === "applied" && options.onKeepHunk && options.onRevertHunk) {
    markers.hunks.forEach((hunk, index) => {
      const element = document.createElement("div");
      element.className = "weavie-inline-pending-tag";
      const actions = documentModel.actions.captureHunk(hunk, presentation);
      element.append(
        track(
          makeButton(
            "weavie-inline-pending-keep",
            "✓ keep",
            withShortcut("Keep this change", CommandIds.acceptChange),
            actions.keep,
          ),
        ),
        track(
          makeButton(
            "weavie-inline-pending-revert",
            "✕ revert",
            withShortcut("Revert this change", CommandIds.rejectChange),
            actions.revert,
          ),
        ),
      );
      controls.push({
        id: `weavie.pending.${index}`,
        line: reviewHunkLine(hunk.anchorLine, documentModel.model.getLineCount()),
        element,
      });
    });
  }
  if (options?.onUnkeepHunk) {
    markers.acceptedHunks.forEach((hunk, index) => {
      const element = document.createElement("div");
      element.className = "weavie-inline-accepted-tag";
      const kept = document.createElement("span");
      kept.className = "weavie-inline-accepted-kept";
      kept.textContent = "✓ accepted";
      element.append(
        kept,
        track(
          makeButton(
            "weavie-inline-accepted-undo",
            "↶ undo",
            withShortcut("Undo keep", CommandIds.undoKeep),
            documentModel.actions.captureUnkeep(hunk, presentation),
          ),
        ),
      );
      controls.push({
        id: `weavie.accepted.${index}`,
        line: reviewHunkLine(hunk.anchorLine, documentModel.model.getLineCount()),
        element,
      });
    });
  }
  return controls;
}
