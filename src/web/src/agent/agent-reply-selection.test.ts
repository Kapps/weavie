import { describe, expect, it, vi } from "vitest";

vi.mock("./selection-actions", () => ({ createSelectionActions: vi.fn() }));

const { selectionLabel } = await import("./agent-reply-selection");

describe("selection labels", () => {
  it("keeps short text on one line and shortens long text", () => {
    expect(selectionLabel("  two\n lines  ")).toBe("two lines");
    const long = selectionLabel("x".repeat(80));
    expect(long).toHaveLength(60);
    expect(long.endsWith("…")).toBe(true);
  });
});
