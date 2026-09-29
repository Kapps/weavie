import { Range } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/core/range";
import type { IModelDecoration } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model";
import {
  ModelDecorationOptions,
  type TextModel,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import { describe, expect, it, vi } from "vitest";
import { captureReviewDecorations, sameReviewDecorations } from "./review-projection-decorations";

function fixture() {
  const full = new Range(1, 1, 2, 10);
  const stored = ModelDecorationOptions.createDynamic({
    description: "global",
    before: { content: "hint" },
    lineHeight: 1.5,
  });
  let decorations: IModelDecoration[] = [
    {
      id: "stored",
      ownerId: 0,
      range: new Range(1, 1, 1, 2),
      options: stored as IModelDecoration["options"],
    },
  ];
  let className = "bracket1";
  const query = vi.fn((range: Range) => {
    expect(range).toBe(full);
    return [
      ...decorations,
      {
        id: "provider",
        ownerId: 0,
        range: new Range(2, 8, 2, 9),
        options: { description: "provider", inlineClassName: className },
      },
    ];
  });
  const model = {
    getFullModelRange: () => full,
    getDecorationsInRange: query,
  } as unknown as TextModel;
  return {
    model,
    query,
    decorations: () => decorations,
    set: (next: IModelDecoration[]) => {
      decorations = next;
    },
    recolor: () => {
      className = "bracket2";
    },
  };
}

describe("passive projection decoration currency", () => {
  it("ignores non-rendering tracking ranges without relying on descriptions or a visual-property whitelist", () => {
    const data = fixture();
    const before = captureReviewDecorations(data.model);
    const tracked = [0, 1, 2, 3].map((stickiness) => ({
      id: `tracked:${stickiness}`,
      ownerId: 0,
      range: new Range(1, 1, 1, 1),
      options: ModelDecorationOptions.createDynamic({
        description: "arbitrary owner",
        stickiness,
      }) as IModelDecoration["options"],
    }));
    data.set([...tracked, ...data.decorations()]);
    expect(sameReviewDecorations(before, captureReviewDecorations(data.model))).toBe(true);
    data.set([
      ...data.decorations(),
      {
        ...tracked[0]!,
        id: "visual",
        options: ModelDecorationOptions.createDynamic({
          description: "arbitrary owner",
          inlineClassName: "visible",
        }) as IModelDecoration["options"],
      },
    ]);
    expect(sameReviewDecorations(before, captureReviewDecorations(data.model))).toBe(false);
  });
  it("compares synthesized options by value through the last column while ignoring other editor owners", () => {
    const data = fixture();
    const before = captureReviewDecorations(data.model);
    expect(before).toHaveLength(2);
    data.set([...data.decorations(), { ...data.decorations()[0]!, id: "editor", ownerId: 42 }]);
    expect(sameReviewDecorations(before, captureReviewDecorations(data.model))).toBe(true);
    data.recolor();
    expect(sameReviewDecorations(before, captureReviewDecorations(data.model))).toBe(false);
  });

  it("copies mutable ranges and detects global injection, height, and lifetime changes", () => {
    const data = fixture();
    const before = captureReviewDecorations(data.model);
    const item = data.decorations()[0]!;
    (item.range as { endColumn: number }).endColumn = 3;
    expect(sameReviewDecorations(before, captureReviewDecorations(data.model))).toBe(false);
    (item.range as { endColumn: number }).endColumn = 2;
    for (const options of [{ before: { content: "different" } }, { lineHeight: 2 }]) {
      data.set([
        {
          ...item,
          options: ModelDecorationOptions.createDynamic({
            description: "global",
            ...options,
          }) as IModelDecoration["options"],
        },
      ]);
      expect(sameReviewDecorations(before, captureReviewDecorations(data.model))).toBe(false);
    }
    data.set([]);
    expect(sameReviewDecorations(before, captureReviewDecorations(data.model))).toBe(false);
  });

  it("does not flatten ordering of coincident global decorations", () => {
    const data = fixture();
    const first = data.decorations()[0]!;
    const second = { ...first, id: "other" };
    data.set([first, second]);
    const before = captureReviewDecorations(data.model);
    data.set([second, first]);
    expect(sameReviewDecorations(before, captureReviewDecorations(data.model))).toBe(false);
  });
});
