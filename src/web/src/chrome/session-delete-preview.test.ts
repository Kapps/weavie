import { describe, expect, it } from "vitest";
import { parseDeleteSessionPreview } from "./session-delete-preview";

const validPreview = {
  revision: "revision-1",
  label: "feature",
  removesCheckout: true,
  worktree: {
    state: "modified",
    branchless: false,
    changedFiles: ["src/app.ts"],
    changedCount: 1,
  },
  drafts: [{ path: "/scratch/Untitled-1", name: "Untitled-1" }],
};

describe("parseDeleteSessionPreview", () => {
  it("accepts the complete revision-bound worktree and scratch loss set", () => {
    expect(parseDeleteSessionPreview(validPreview)).toEqual(validPreview);
  });

  it.each([
    undefined,
    { ...validPreview, revision: undefined },
    { ...validPreview, worktree: { ...validPreview.worktree, state: "unknown" } },
    { ...validPreview, worktree: { ...validPreview.worktree, changedCount: 0 } },
    { ...validPreview, drafts: [{ path: "", name: "Untitled-1" }] },
  ])("rejects incomplete or internally inconsistent previews", (value) => {
    expect(() => parseDeleteSessionPreview(value)).toThrow("invalid deletion preview");
  });
});
