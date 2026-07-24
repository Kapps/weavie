import { describe, expect, it, vi } from "vitest";

vi.mock("../monaco-setup", () => ({
  monaco: {
    Range: class {
      constructor(
        readonly startLineNumber: number,
        readonly startColumn: number,
        readonly endLineNumber: number,
        readonly endColumn: number,
      ) {}
    },
  },
}));

import { applySuggestion } from "./spell-actions";

const currentIssue = {
  line: 1,
  startColumn: 1,
  endColumn: 4,
  word: "teh",
};

function createHarness() {
  const executeEdits = vi.fn(() => true);
  const contextAt = vi.fn(() => currentIssue);
  const isCurrentContext = vi.fn((context: typeof currentIssue) => context === currentIssue);
  return {
    editor: {
      getPosition: () => ({ lineNumber: 1, column: 2 }),
      executeEdits,
    },
    state: { contextAt, isCurrentContext },
    executeEdits,
    contextAt,
  };
}

describe("applySuggestion", () => {
  it("applies a replacement-only command to the issue under the cursor", () => {
    const harness = createHarness();

    expect(
      applySuggestion(harness.editor as never, harness.state as never, { replacement: "the" }),
    ).toBe(true);
    expect(harness.contextAt).toHaveBeenCalledWith({ lineNumber: 1, column: 2 });
    expect(harness.executeEdits).toHaveBeenCalledWith("weavie-spelling", [
      {
        range: expect.objectContaining({
          startLineNumber: 1,
          startColumn: 1,
          endLineNumber: 1,
          endColumn: 4,
        }),
        text: "the",
      },
    ]);
  });

  it("does not fall back to the cursor when explicit context is stale", () => {
    const harness = createHarness();

    expect(
      applySuggestion(harness.editor as never, harness.state as never, {
        ...currentIssue,
        word: "stale",
        replacement: "the",
      }),
    ).toBe(false);
    expect(harness.contextAt).not.toHaveBeenCalled();
    expect(harness.executeEdits).not.toHaveBeenCalled();
  });
});
