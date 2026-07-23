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
    editor: { TrackedRangeStickiness: { NeverGrowsWhenTypingAtEdges: 0 } },
  },
}));

import { SpellModel } from "./spell-model";

describe("SpellModel lifecycle", () => {
  it("releases Monaco listeners when its editor host is disposed", () => {
    const contentListener = { dispose: vi.fn() };
    const disposalListener = { dispose: vi.fn() };
    const model = {
      onDidChangeContent: vi.fn(() => contentListener),
      onWillDispose: vi.fn(() => disposalListener),
    };
    const state = new SpellModel(
      model as never,
      () => "epoch",
      () => "anchor",
    );

    state.track(vi.fn(), vi.fn(), vi.fn(), vi.fn());
    state.dispose();

    expect(contentListener.dispose).toHaveBeenCalledOnce();
    expect(disposalListener.dispose).toHaveBeenCalledOnce();
  });

  it("merges only exact restored lines and reuses live anchors", () => {
    const lines = ["teh", "unchanged"];
    const decorations = new Map<string, { startLineNumber: number }>();
    let nextDecoration = 0;
    let nextAnchor = 0;
    const model = {
      getLineCount: () => lines.length,
      getLineContent: (line: number) => lines[line - 1],
      getLineMaxColumn: (line: number) => (lines[line - 1]?.length ?? 0) + 1,
      getDecorationRange: (id: string) => decorations.get(id) ?? null,
      deltaDecorations: vi.fn(
        (oldIds: string[], additions: { range: { startLineNumber: number } }[]): string[] => {
          for (const id of oldIds) {
            decorations.delete(id);
          }
          return additions.map((addition) => {
            const id = `decoration-${++nextDecoration}`;
            decorations.set(id, { startLineNumber: addition.range.startLineNumber });
            return id;
          });
        },
      ),
    };
    const state = new SpellModel(
      model as never,
      () => "epoch",
      () => `anchor-${++nextAnchor}`,
    );

    expect(
      state.restoreAuthoredLines([
        { line: 1, text: "teh" },
        { line: 2, text: "different" },
        { line: 3, text: "out of range" },
      ]),
    ).toEqual(["anchor-1"]);
    expect(state.restoreAuthoredLines([{ line: 1, text: "teh" }])).toEqual(["anchor-1"]);
    expect(state.restoreAuthoredLines([{ line: 2, text: "unchanged" }])).toEqual(["anchor-2"]);
    expect([...state.anchors.keys()]).toEqual(["anchor-1", "anchor-2"]);
    expect(model.deltaDecorations).toHaveBeenCalledTimes(2);
  });
});
