import { describe, expect, it } from "vitest";
import { chooseOpenSlot } from "./open-target";

describe("chooseOpenSlot", () => {
  it("uses the selected session when it belongs to the backend that was handed the path", () => {
    expect(
      chooseOpenSlot(
        { backendId: "local", slot: "worktree-a" },
        {
          backendId: "local",
          fallbackSlot: "workspace",
        },
      ),
    ).toBe("worktree-a");
  });

  it("falls to that backend's checkout when the selection is on another backend", () => {
    // A local file is not readable from a session running on another machine, so following selection there
    // would open nothing.
    expect(
      chooseOpenSlot(
        { backendId: "remote-1", slot: "worktree-b" },
        {
          backendId: "local",
          fallbackSlot: "workspace",
        },
      ),
    ).toBe("workspace");
  });

  it("uses the checkout when nothing is selected", () => {
    expect(chooseOpenSlot(null, { backendId: "local", fallbackSlot: "workspace" })).toBe(
      "workspace",
    );
  });

  it("has nowhere to go when the backend named no checkout", () => {
    expect(
      chooseOpenSlot(
        { backendId: "remote-1", slot: "x" },
        { backendId: "local", fallbackSlot: null },
      ),
    ).toBeNull();
  });
});
