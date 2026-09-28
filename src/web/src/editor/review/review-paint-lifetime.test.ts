import { beforeEach, describe, expect, it, vi } from "vitest";

const resources = vi.hoisted(() => ({
  live: new Set<string>(),
  add(name: string) {
    this.live.add(name);
    return { dispose: () => this.live.delete(name) };
  },
}));
vi.mock("../../bridge", () => ({ log: vi.fn() }));
vi.mock("../../commands/keybindings", () => ({ IS_MAC: false }));
vi.mock("../../fonts", () => ({ onFontsChanged: () => resources.add("fonts").dispose }));
vi.mock("../monaco-setup", () => ({ monaco: {} }));
vi.mock("../session-uri", () => ({ sessionFileUri: vi.fn() }));
vi.mock("./diff-zones", () => ({ DIFF_RECOMPUTE_DEBOUNCE_MS: 50, addDiffZones: vi.fn() }));
vi.mock("./review-toolbar", () => ({ makeButton: vi.fn(), withShortcut: vi.fn() }));
vi.mock("./review-toolbar-presenter", () => ({ createReviewToolbarPresenter: vi.fn() }));
vi.mock("./review-document", () => ({ ReviewDocumentScope: vi.fn() }));
vi.mock("./review-comment-view", () => ({
  createReviewCommentView: () => ({
    ...resources.add("comments"),
    configure: vi.fn(),
    focused: () => false,
    retained: () => false,
  }),
}));

import { createInlineDiffPaint } from "../inline-diff";

describe("initial inline-diff paint failure", () => {
  beforeEach(() => resources.live.clear());

  it.each([
    "none",
    "file",
  ])("releases listeners, comment ownership and source leases after initial %s target failure", (kind) => {
    const model = { uri: { toString: () => "model:owned" }, dispose: vi.fn() };
    const document = { model, retainSources: () => resources.add("sources") };
    const scope = {
      get: () => ({ mode: "applied" }),
      has: () => true,
      forModel: () => document,
      onDidChangeConfiguration: () => resources.add("configuration"),
    };
    const editor = {
      getModel: () => model,
      onDidChangeModel: () => resources.add("model"),
      onDidChangeModelContent: () => resources.add("content"),
      onDidChangeCursorPosition: () => resources.add("cursor"),
      onDidScrollChange: () => resources.add("scroll"),
    };
    const presentation = {
      updateGeometry: (change: () => void) => change(),
      scope: { current: "file" },
    };
    const failure = new Error("initial publish failed");
    let failed = false;
    expect(() =>
      createInlineDiffPaint(
        editor as unknown as Parameters<typeof createInlineDiffPaint>[0],
        presentation as Parameters<typeof createInlineDiffPaint>[1],
        scope as unknown as Parameters<typeof createInlineDiffPaint>[2],
        (target) => {
          if (!failed && target.kind === kind) {
            failed = true;
            throw failure;
          }
        },
      ),
    ).toThrow(failure);
    expect(failed).toBe(true);
    expect(resources.live).toEqual(new Set());
    expect(model.dispose).not.toHaveBeenCalled();
  });
});
