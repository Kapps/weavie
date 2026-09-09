import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";
import { CommandIds } from "../../commands/types";
import type { EditorController } from "../editor-controller";
import type { TabOwner } from "../tab-owner";

const env = vi.hoisted(() => ({
  selected: null as ClientSession | null,
  tab: undefined as TabOwner | undefined,
}));
vi.mock("../../bridge", () => ({ selectedSession: () => env.selected }));
vi.mock("../session-store", () => ({
  activeTabFor: (session: ClientSession) => (session === env.selected ? env.tab : undefined),
}));
vi.mock("../tab-command-bindings", () => ({
  commandPath: (args: { path?: string } | undefined) => args?.path,
}));
const { reviewCommandBindings } = await import("./review-commands");

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
  toggleFileCollapsed: vi.fn(() => true),
};
const openReview = vi.fn((session: ClientSession) => session === env.selected);
const lifetime = new AbortController();
const presentation = {
  signal: lifetime.signal,
  actions: () => inline,
  capture: () => ({ state: null, text: { path: "/left/one.ts", line: 9 } }),
};
const editor = { review, openReview } as unknown as EditorController;
const bindings = new Map(reviewCommandBindings(editor));
const capture = (id: string, args: unknown, session: ClientSession) =>
  bindings.get(id)!({ session }, args);
const run = async (id: string, args: unknown, session: ClientSession): Promise<unknown> =>
  capture(id, args, session)(args, { session });

beforeEach(() => {
  vi.clearAllMocks();
  env.selected = left;
  env.tab = { presentation } as unknown as TabOwner;
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
      CommandIds.reviewToggleFile,
      CommandIds.reviewNextFile,
      CommandIds.reviewPrevFile,
    ];

    for (const id of ids) {
      expect(await run(id, { path: "/right/one.ts", line: 9 }, right)).toBe(false);
    }
    expect(openReview).toHaveBeenCalledWith(right, "/right/one.ts", 9);
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

it("keeps a captured file action addressed after switching tabs and sessions", () => {
  const keep = capture(CommandIds.keepFile, undefined, left);
  env.selected = right;
  env.tab = { presentation } as unknown as TabOwner;
  keep(undefined, { session: right });
  expect(review.keepFile).toHaveBeenCalledWith(left, "/left/one.ts");
});

it("rejects a captured presentation action after its tab is replaced", () => {
  const accept = capture(CommandIds.acceptChange, undefined, left);
  env.tab = { presentation } as unknown as TabOwner;
  expect(() => accept(undefined, { session: left })).toThrow("no longer displayed");
  expect(inline.accept).not.toHaveBeenCalled();
});
