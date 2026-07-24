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

import { SpellModel } from "./spell-model";

describe("SpellModel", () => {
  it("releases Monaco listeners when its editor host is disposed", () => {
    const disposalListener = { dispose: vi.fn() };
    const model = {
      onWillDispose: vi.fn(() => disposalListener),
    };
    const state = new SpellModel(model as never);

    state.track(vi.fn());
    state.dispose();

    expect(disposalListener.dispose).toHaveBeenCalledOnce();
  });

  it("accepts only diagnostics matching the current revision and word span", () => {
    const lines = ['const value = "teh";'];
    const model = {
      getVersionId: () => 4,
      getLineCount: () => lines.length,
      getLineContent: (line: number) => lines[line - 1],
      getLineMaxColumn: (line: number) => (lines[line - 1]?.length ?? 0) + 1,
      getValueInRange: (range: {
        startLineNumber: number;
        startColumn: number;
        endColumn: number;
      }) => lines[range.startLineNumber - 1]?.slice(range.startColumn - 1, range.endColumn - 1),
    };
    const state = new SpellModel(model as never);
    state.markSubmitted(4);
    const issue = {
      line: 1,
      startColumn: 16,
      endColumn: 19,
      word: "teh",
    };

    expect(
      state.applyDiagnostics(
        [issue, { ...issue, word: "the" }, { ...issue, startColumn: 15, endColumn: 18 }],
        4,
      ),
    ).toBe(true);
    expect(state.decorations()).toHaveLength(1);
    expect(state.contextAt({ lineNumber: 1, column: 17 })).toEqual(issue);
    expect(state.contextAt({ lineNumber: 1, column: issue.endColumn })).toEqual(issue);
    expect(state.contextAt({ lineNumber: 1, column: issue.endColumn + 1 })).toBeNull();
    expect(state.isCurrentContext(issue)).toBe(true);

    expect(state.applyDiagnostics([], 3)).toBe(false);
    expect(state.decorations()).toHaveLength(1);
    state.clear();
    expect(state.applyDiagnostics([issue], 4)).toBe(false);
    expect(state.decorations()).toHaveLength(0);
    lines[0] = 'const value = "the";';
    expect(state.isCurrentContext(issue)).toBe(false);
  });
});
