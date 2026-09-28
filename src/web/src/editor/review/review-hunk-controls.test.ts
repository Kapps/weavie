import { afterEach, describe, expect, it, vi } from "vitest";
import type { DiffMarkers } from "./diff-markers";
import type { ReviewDocument } from "./review-document";
import type { ReviewActionPresentation } from "./review-file-actions";
import { buildReviewHunkControls } from "./review-hunk-controls";

vi.mock("./review-toolbar", () => ({
  makeButton: (_className: string, _label: string, _title: string, action: () => void) => ({
    click: action,
  }),
  withShortcut: (label: string) => label,
}));

afterEach(() => vi.unstubAllGlobals());

describe("review hunk control anchors", () => {
  it.each([
    1, 10,
  ])("clamps EOF deletion controls to line %i without rewriting action coordinates", (lineCount) => {
    vi.stubGlobal("document", { createElement: () => ({ append: vi.fn() }) });
    const pending = {
      anchorLine: lineCount + 1,
      currentStart: lineCount + 1,
      currentEndExclusive: lineCount + 1,
      baselineStart: lineCount + 1,
      baselineEndExclusive: lineCount + 3,
    };
    const accepted = {
      anchorLine: lineCount + 1,
      acceptedStart: lineCount + 1,
      acceptedEndExclusive: lineCount + 1,
      reviewStart: lineCount + 1,
      reviewEndExclusive: lineCount + 3,
      acceptedGuardText: "",
      guardText: "deleted\nlines",
    };
    const keep = vi.fn();
    const revert = vi.fn();
    const undo = vi.fn();
    const captureHunk = vi.fn(() => ({ keep, revert }));
    const captureUnkeep = vi.fn(() => undo);
    const documentModel = {
      model: { getLineCount: () => lineCount },
      actions: {
        options: {
          mode: "applied",
          onKeepHunk: vi.fn(),
          onRevertHunk: vi.fn(),
          onUnkeepHunk: vi.fn(),
        },
        captureHunk,
        captureUnkeep,
      },
    } as unknown as ReviewDocument;
    const presentation = {} as ReviewActionPresentation;
    const buttons: HTMLButtonElement[] = [];
    const controls = buildReviewHunkControls(
      documentModel,
      presentation,
      { hunks: [pending], acceptedHunks: [accepted] } as DiffMarkers,
      (button) => {
        buttons.push(button);
        return button;
      },
    );
    expect(controls.map((control) => control.line)).toEqual([lineCount, lineCount]);
    expect(captureHunk).toHaveBeenCalledExactlyOnceWith(pending, presentation);
    expect(captureUnkeep).toHaveBeenCalledExactlyOnceWith(accepted, presentation);
    for (const button of buttons) button.click();
    expect(keep).toHaveBeenCalledOnce();
    expect(revert).toHaveBeenCalledOnce();
    expect(undo).toHaveBeenCalledOnce();
    expect(pending.anchorLine).toBe(lineCount + 1);
    expect(accepted.anchorLine).toBe(lineCount + 1);
  });
});
