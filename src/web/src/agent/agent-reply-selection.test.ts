import { describe, expect, it, vi } from "vitest";

vi.mock("./composer-store", () => ({ attachAgentSelection: vi.fn() }));
vi.mock("../chrome/agent-default", () => ({ agentAttachOnSelect: () => true }));
vi.mock("../commands/context", () => ({ setContext: vi.fn() }));
vi.mock("../commands/registry", () => ({ registerCommand: vi.fn() }));

const { selectionLabel } = await import("./agent-reply-selection");

describe("selection labels", () => {
  it("keeps short text on one line and shortens long text", () => {
    expect(selectionLabel("  two\n lines  ")).toBe("two lines");
    const long = selectionLabel("x".repeat(80));
    expect(long).toHaveLength(60);
    expect(long.endsWith("…")).toBe(true);
  });
});
