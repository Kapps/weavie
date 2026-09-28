import { beforeEach, describe, expect, it, vi } from "vitest";

const calculation = vi.hoisted(() => ({ compute: vi.fn(), dispose: vi.fn() }));
const tokenization = vi.hoisted(() => ({ whenAccurate: vi.fn(), dispose: vi.fn() }));
vi.mock("./review-tokenization", () => ({
  ReviewTokenization: class {
    whenAccurate = tokenization.whenAccurate;
    dispose = tokenization.dispose;
  },
}));
vi.mock("@codingame/monaco-vscode-api", () => ({}));
vi.mock("@codingame/monaco-vscode-api/services", () => ({}));
vi.mock("./diff-computer", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./diff-computer")>()),
  DiffComputer: class {
    compute = calculation.compute;
    dispose = calculation.dispose;
  },
}));
vi.mock("../monaco-setup", () => ({
  monaco: {
    Range: class {
      constructor(
        public startLineNumber: number,
        public startColumn: number,
        public endLineNumber: number,
        public endColumn: number,
      ) {}
    },
    editor: { OverviewRulerLane: { Left: 1 } },
  },
}));

import { ReviewDocument } from "./review-document";
import { preparePassiveDocument } from "./review-passive-document";
import type { ReviewFileDiff } from "./review-store";

function fixture() {
  let version = 1;
  const lines = [
    "new first",
    "same second",
    "same third",
    "same fourth",
    "same fifth",
    "same sixth",
  ];
  const model = {
    uri: { toString: () => "weavie-file:/source.ts" },
    getVersionId: () => version,
    getLanguageId: () => "typescript",
    getLineCount: () => lines.length,
    getLineContent: (line: number) => lines[line - 1]!,
    isDisposed: () => false,
    isTooLargeForTokenization: () => false,
  };
  const diff: ReviewFileDiff = {
    revision: "1",
    rejected: [],
    path: "/source.ts",
    name: "source.ts",
    baseline: ["old first", ...lines.slice(1)].join("\n"),
    baselineExists: true,
    acceptedBaseline: ["old first", ...lines.slice(1)].join("\n"),
    acceptedBaselineExists: true,
    current: lines.join("\n"),
    currentExists: true,
  };
  const result = {
    status: "ready",
    changes: [
      {
        original: { startLineNumber: 1, endLineNumberExclusive: 2, isEmpty: false },
        modified: { startLineNumber: 1, endLineNumberExclusive: 2, isEmpty: false },
        innerChanges: [],
      },
    ],
    userChanges: [],
    fadedChanges: [],
  };
  return {
    model,
    document: new ReviewDocument(model as never, () => undefined),
    diff,
    result,
    change: () => version++,
  };
}

describe("passive review document preparation", () => {
  beforeEach(() => {
    calculation.compute.mockReset();
    calculation.dispose.mockReset();
    tokenization.whenAccurate.mockReset().mockResolvedValue(undefined);
    tokenization.dispose.mockReset();
  });

  it("reuses a synchronous cached diff and preserves deleted rows plus model line numbers", async () => {
    const { model, document: owner, diff, result } = fixture();
    calculation.compute.mockReturnValue(result);
    const document = await preparePassiveDocument(owner, diff, new AbortController().signal);
    expect(document.model).toBe(model);
    expect(document.rows.map((row) => [row.line, row.text, row.removed])).toEqual([
      [0, "old first", true],
      [1, "new first", false],
      [2, "same second", false],
      [3, "same third", false],
      [4, "same fourth", false],
    ]);
    expect(calculation.dispose).toHaveBeenCalledOnce();
    expect(tokenization.whenAccurate).toHaveBeenCalledExactlyOnceWith(1, expect.any(AbortSignal));
  });

  it("does not publish geometry for a model changed while the worker was running", async () => {
    const { document, diff, result, change } = fixture();
    let finish!: (result: unknown) => void;
    calculation.compute.mockReturnValue(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
    const work = preparePassiveDocument(document, diff, new AbortController().signal);
    change();
    finish(result);
    await expect(work).rejects.toMatchObject({ name: "AbortError" });
    expect(calculation.dispose).toHaveBeenCalledOnce();
    expect(tokenization.whenAccurate).not.toHaveBeenCalled();
  });

  it("keeps all model rows when Monaco uses its unprojected large-file view", async () => {
    const { model, document, diff, result } = fixture();
    model.isTooLargeForTokenization = () => true;
    calculation.compute.mockReturnValue(result);
    const prepared = await preparePassiveDocument(document, diff, new AbortController().signal);
    expect(prepared.rows.filter((row) => !row.removed).map((row) => row.line)).toEqual([
      1, 2, 3, 4, 5, 6,
    ]);
  });

  it("rejects a same-version language replacement after token accuracy resolves", async () => {
    const { model, document, diff, result } = fixture();
    calculation.compute.mockReturnValue(result);
    tokenization.whenAccurate.mockImplementation(async () => {
      model.getLanguageId = () => "python";
    });
    await expect(
      preparePassiveDocument(document, diff, new AbortController().signal),
    ).rejects.toMatchObject({ name: "AbortError" });
  });

  it("releases source models on worker failure without publishing stale text", async () => {
    const { document, diff } = fixture();
    calculation.compute.mockRejectedValue(new Error("worker failed"));
    await expect(
      preparePassiveDocument(document, diff, new AbortController().signal),
    ).rejects.toThrow("worker failed");
    expect(calculation.dispose).toHaveBeenCalledOnce();
  });

  it("does not publish a cancelled document even when its calculation completed", async () => {
    const { document, diff, result } = fixture();
    calculation.compute.mockReturnValue(result);
    const lifetime = new AbortController();
    const work = preparePassiveDocument(document, diff, lifetime.signal);
    lifetime.abort();
    await expect(work).rejects.toMatchObject({ name: "AbortError" });
    expect(calculation.dispose).toHaveBeenCalledOnce();
    expect(tokenization.whenAccurate).not.toHaveBeenCalled();
  });
});
