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

import { applySuggestion, wordForDictionary } from "./spell-actions";

const currentIssue = {
  line: 1,
  startColumn: 1,
  endColumn: 4,
  word: "teh",
  modelId: "model-a",
};

function createHarness() {
  const executeEdits = vi.fn(() => true);
  const contextAt = vi.fn(() => currentIssue);
  const isCurrentContext = vi.fn(
    (context: typeof currentIssue) =>
      context.line === currentIssue.line &&
      context.startColumn === currentIssue.startColumn &&
      context.endColumn === currentIssue.endColumn &&
      context.word === currentIssue.word &&
      context.modelId === currentIssue.modelId,
  );
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

  it("rejects an explicit context owned by another model", () => {
    const harness = createHarness();

    expect(
      applySuggestion(harness.editor as never, harness.state as never, {
        ...currentIssue,
        modelId: "model-b",
        replacement: "the",
      }),
    ).toBe(false);
    expect(harness.contextAt).not.toHaveBeenCalled();
    expect(harness.executeEdits).not.toHaveBeenCalled();
  });
});

describe("wordForDictionary", () => {
  it("accepts a direct word without an editor context", () => {
    const harness = createHarness();

    expect(
      wordForDictionary(harness.editor as never, harness.state as never, { word: "Weavie" }),
    ).toBe("Weavie");
    expect(harness.contextAt).not.toHaveBeenCalled();
  });

  it("rejects context-shaped arguments that omit model ownership", () => {
    const harness = createHarness();

    expect(
      wordForDictionary(harness.editor as never, harness.state as never, {
        line: currentIssue.line,
        startColumn: currentIssue.startColumn,
        endColumn: currentIssue.endColumn,
        word: currentIssue.word,
      }),
    ).toBeNull();
    expect(harness.contextAt).not.toHaveBeenCalled();
  });

  it("rejects an explicit context owned by another model", () => {
    const harness = createHarness();

    expect(
      wordForDictionary(harness.editor as never, harness.state as never, {
        ...currentIssue,
        modelId: "model-b",
      }),
    ).toBeNull();
    expect(harness.contextAt).not.toHaveBeenCalled();
  });
});
