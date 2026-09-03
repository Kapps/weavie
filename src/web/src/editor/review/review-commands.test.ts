import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";
import { CommandIds } from "../../commands/types";
import type { EditorController } from "../editor-controller";
import { reviewCommandBindings } from "./review-commands";

const left = { address: { slot: "left", incarnation: "1" } } as ClientSession;
const right = { address: { slot: "right", incarnation: "1" } } as ClientSession;

const inline = {
  nextChange: vi.fn(() => true),
  prevChange: vi.fn(() => true),
  accept: vi.fn(() => true),
  reject: vi.fn(() => true),
  comment: vi.fn(() => true),
  nextFile: vi.fn(() => true),
  prevFile: vi.fn(() => true),
  undoKeep: vi.fn(() => true),
  undoRevert: vi.fn(() => true),
};
const review = {
  revert: vi.fn(() => true),
  keepFile: vi.fn(() => true),
  revertFile: vi.fn(() => true),
  keepAll: vi.fn(() => true),
  undoKeep: vi.fn(() => true),
  undoRevert: vi.fn(() => true),
  redo: vi.fn(() => true),
  toggleMode: vi.fn(() => true),
  toggleFileCollapsed: vi.fn(() => true),
};
const openReview = vi.fn(() => true);
const editor = { inline, review, openReview } as unknown as Pick<
  EditorController,
  "inline" | "openReview" | "review"
>;
const bindings = new Map(reviewCommandBindings(editor, () => left));
const run = async (id: string, args: unknown, session: ClientSession): Promise<unknown> =>
  bindings.get(id)?.(args, { session });

beforeEach(() => {
  vi.clearAllMocks();
});

describe("review command bindings", () => {
  it("routes mutations to the captured session even while another session is selected", async () => {
    await run(CommandIds.undoChange, undefined, right);
    await run(CommandIds.keepFile, { path: "/right/one.ts" }, right);
    await run(CommandIds.revertFile, { path: "/right/two.ts" }, right);
    await run(CommandIds.keepAll, undefined, right);
    await run(CommandIds.undoKeep, undefined, right);
    await run(CommandIds.undoRevert, undefined, right);
    await run(CommandIds.redoReview, undefined, right);

    expect(review.revert).toHaveBeenCalledWith(right);
    expect(review.keepFile).toHaveBeenCalledWith(right, "/right/one.ts");
    expect(review.revertFile).toHaveBeenCalledWith(right, "/right/two.ts");
    expect(review.keepAll).toHaveBeenCalledWith(right);
    expect(review.undoKeep).toHaveBeenCalledWith(right);
    expect(review.undoRevert).toHaveBeenCalledWith(right);
    expect(review.redo).toHaveBeenCalledWith(right);
  });

  it("declines presentation actions from an unfocused session", async () => {
    const ids = [
      CommandIds.nextChange,
      CommandIds.prevChange,
      CommandIds.acceptChange,
      CommandIds.rejectChange,
      CommandIds.reviewComment,
      CommandIds.reviewOpen,
      CommandIds.reviewToggleMode,
      CommandIds.reviewToggleFile,
      CommandIds.reviewNextFile,
      CommandIds.reviewPrevFile,
    ];

    for (const id of ids) {
      expect(await run(id, { path: "/right/one.ts", line: 9 }, right)).toBe(false);
    }
    expect(openReview).not.toHaveBeenCalled();
    expect(review.toggleMode).not.toHaveBeenCalled();
    expect(review.toggleFileCollapsed).not.toHaveBeenCalled();
    expect(Object.values(inline).every((action) => action.mock.calls.length === 0)).toBe(true);
  });

  it("opens the captured selected session at an exact file and line", async () => {
    expect(await run(CommandIds.reviewOpen, { path: "/left/one.ts", line: 17 }, left)).toBe(true);
    expect(openReview).toHaveBeenCalledWith(left, "/left/one.ts", 17);
  });

  it("toggles the addressed file fold for the selected session", async () => {
    expect(await run(CommandIds.reviewToggleFile, { path: "/left/one.ts" }, left)).toBe(true);
    expect(review.toggleFileCollapsed).toHaveBeenCalledWith(left, "/left/one.ts");
  });

  it("lets the selected presentation consume empty undo chords", async () => {
    expect(await run(CommandIds.undoKeep, undefined, left)).toBe(true);
    expect(await run(CommandIds.undoRevert, undefined, left)).toBe(true);
    expect(inline.undoKeep).toHaveBeenCalledOnce();
    expect(inline.undoRevert).toHaveBeenCalledOnce();
    expect(review.undoKeep).not.toHaveBeenCalled();
    expect(review.undoRevert).not.toHaveBeenCalled();
  });
});
