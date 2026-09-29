import { Emitter } from "@codingame/monaco-vscode-api/vscode/vs/base/common/event";
import type { TextModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const grammar = vi.hoisted(() => vi.fn());
const registry = vi.hoisted(() => ({ get: vi.fn(), isResolved: vi.fn(), factories: new Map() }));
vi.mock("@codingame/monaco-vscode-api/vscode/vs/editor/common/languages", () => ({
  TokenizationRegistry: {
    getOrCreate: grammar,
    get: registry.get,
    isResolved: registry.isResolved,
    _factories: registry.factories,
  },
}));

import { ReviewTokenization } from "./review-tokenization";

function fixture() {
  let version = 1;
  let language = "javascript";
  let disposed = false;
  const content = new Emitter<void>();
  const languages = new Emitter<void>();
  const disposal = new Emitter<void>();
  const view = { setVisibleLines: vi.fn() };
  const model = {
    onBeforeAttached: vi.fn(() => view),
    onBeforeDetached: vi.fn(),
    onWillDispose: disposal.event,
    onDidChangeContent: content.event,
    onDidChangeLanguage: languages.event,
    getVersionId: () => version,
    getLanguageId: () => language,
    getLineCount: () => 3,
    isDisposed: () => disposed,
    tokenization: {
      tokens: {
        get: (): Pick<TextModel["tokenization"], "hasAccurateTokensForLine"> => model.tokenization,
      },
      hasAccurateTokensForLine: vi.fn((_line: number) => true),
      forceTokenization: vi.fn(),
    },
  };
  const owner = new ReviewTokenization(model as unknown as TextModel);
  return {
    model,
    owner,
    view,
    edit: () => {
      version++;
      content.fire();
    },
    language: () => {
      language = "typescript";
      languages.fire();
    },
    dispose: () => {
      disposal.fire();
      disposed = true;
    },
  };
}

describe("review-owned background tokenization", () => {
  let serial = 0;
  const frames = new Map<number, FrameRequestCallback>();
  const tick = async (): Promise<void> => {
    await Promise.resolve();
    const pending = [...frames];
    frames.clear();
    for (const [, frame] of pending) frame(0);
    await Promise.resolve();
  };
  beforeEach(() => {
    grammar.mockReset().mockResolvedValue(null);
    registry.get.mockReset().mockReturnValue(null);
    registry.isResolved.mockReset().mockReturnValue(true);
    registry.factories.clear();
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      const id = ++serial;
      frames.set(id, callback);
      return id;
    });
    vi.stubGlobal("cancelAnimationFrame", (id: number) => frames.delete(id));
  });
  afterEach(() => {
    expect(frames.size).toBe(0);
    vi.unstubAllGlobals();
  });

  it("waits for the grammar before consulting accuracy and never forces visible-line work", async () => {
    const f = fixture();
    let load!: () => void;
    registry.isResolved.mockReturnValue(false);
    grammar.mockReturnValue(
      new Promise<void>((resolve) => {
        load = () => {
          registry.isResolved.mockReturnValue(true);
          resolve();
        };
      }),
    );
    const ready = f.owner.whenAccurate(1, new AbortController().signal);
    await tick();
    expect(f.model.tokenization.hasAccurateTokensForLine).not.toHaveBeenCalled();
    await tick();
    await tick();
    expect(grammar).toHaveBeenCalledOnce();
    load();
    await tick();
    await ready;
    expect(f.model.tokenization.hasAccurateTokensForLine.mock.calls).toEqual([[3], [2], [1]]);
    expect(f.model.tokenization.forceTokenization).not.toHaveBeenCalled();
    expect(f.view.setVisibleLines).not.toHaveBeenCalled();
    f.owner.dispose();
    f.owner.dispose();
    expect(f.model.onBeforeAttached).toHaveBeenCalledOnce();
    expect(f.model.onBeforeDetached).toHaveBeenCalledExactlyOnceWith(f.view);
  });

  it("waits for every line without relying on completed state or token-change events", async () => {
    const f = fixture();
    f.model.tokenization.hasAccurateTokensForLine.mockImplementation((line: number) => line !== 2);
    const ready = f.owner.whenAccurate(1, new AbortController().signal);
    await tick();
    expect(frames.size).toBe(1);
    f.model.tokenization.hasAccurateTokensForLine.mockReturnValue(true);
    await tick();
    await ready;
    f.edit();
    f.model.tokenization.hasAccurateTokensForLine.mockReturnValue(false);
    const edited = f.owner.whenAccurate(2, new AbortController().signal);
    await tick();
    expect(frames.size).toBe(1);
    f.model.tokenization.hasAccurateTokensForLine = vi.fn(() => true);
    await tick();
    await edited;
    expect(f.model.onBeforeAttached).toHaveBeenCalledOnce();
    f.owner.dispose();
  });

  it.each([
    "signal",
    "edit",
    "language",
    "model",
    "owner",
  ])("cancels a shared grammar wait immediately on %s and ignores its late completion", async (reason) => {
    const f = fixture();
    const operation = new AbortController();
    let load!: () => void;
    grammar.mockReturnValue(
      new Promise<void>((resolve) => {
        load = resolve;
      }),
    );
    const ready = f.owner.whenAccurate(1, operation.signal);
    const rejected = expect(ready).rejects.toMatchObject({ name: "AbortError" });
    registry.isResolved.mockReturnValue(false);
    await tick();
    if (reason === "signal") operation.abort();
    else if (reason === "edit") f.edit();
    else if (reason === "language") f.language();
    else if (reason === "model") f.dispose();
    else f.owner.dispose();
    await rejected;
    load();
    await tick();
    expect(f.model.tokenization.hasAccurateTokensForLine).not.toHaveBeenCalled();
    f.owner.dispose();
    expect(f.model.onBeforeDetached).toHaveBeenCalledOnce();
  });

  it("cancels polling and rejects a later request when its exact document is retired", async () => {
    const f = fixture();
    f.model.tokenization.hasAccurateTokensForLine.mockReturnValue(false);
    const ready = f.owner.whenAccurate(1, new AbortController().signal);
    const rejected = expect(ready).rejects.toMatchObject({ name: "AbortError" });
    await tick();
    expect(frames.size).toBe(1);
    f.owner.dispose();
    await rejected;
    await expect(f.owner.whenAccurate(1, new AbortController().signal)).rejects.toMatchObject({
      name: "AbortError",
    });
    expect(frames.size).toBe(0);
  });

  it("surfaces grammar failures instead of publishing uncolored readiness", async () => {
    const f = fixture();
    grammar.mockRejectedValue(new Error("grammar failed"));
    registry.isResolved.mockReturnValue(false);
    const rejected = expect(f.owner.whenAccurate(1, new AbortController().signal)).rejects.toThrow(
      "grammar failed",
    );
    await tick();
    await tick();
    await rejected;
    expect(frames.size).toBe(0);
    f.owner.dispose();
  });

  it("surfaces backend exceptions and cancels its polling", async () => {
    const f = fixture();
    f.model.tokenization.hasAccurateTokensForLine.mockImplementation(() => {
      throw new Error("backend failed");
    });
    const rejected = expect(f.owner.whenAccurate(1, new AbortController().signal)).rejects.toThrow(
      "backend failed",
    );
    await tick();
    await rejected;
    expect(frames.size).toBe(0);
    f.owner.dispose();
  });

  it("shares one pending grammar load across cancelled and concurrent preparations", async () => {
    const f = fixture();
    registry.isResolved.mockReturnValue(false);
    grammar.mockReturnValue(new Promise(() => {}));
    for (let index = 0; index < 4; index++) {
      const signal = new AbortController();
      const rejected = expect(f.owner.whenAccurate(1, signal.signal)).rejects.toMatchObject({
        name: "AbortError",
      });
      await tick();
      signal.abort();
      await rejected;
    }
    const first = f.owner.whenAccurate(1, new AbortController().signal);
    const second = f.owner.whenAccurate(1, new AbortController().signal);
    await tick();
    expect(grammar).toHaveBeenCalledOnce();
    registry.isResolved.mockReturnValue(true);
    await tick();
    await Promise.all([first, second]);
    f.owner.dispose();
  });

  it("follows a silently replaced unresolved grammar without accumulating load promises", async () => {
    const f = fixture();
    const first = {};
    registry.factories.set("javascript", first);
    registry.isResolved.mockReturnValue(false);
    let failOld!: (error: unknown) => void;
    grammar.mockReturnValueOnce(
      new Promise((_resolve, reject) => {
        failOld = reject;
      }),
    );
    const ready = f.owner.whenAccurate(1, new AbortController().signal);
    await tick();
    await tick();
    expect(grammar).toHaveBeenCalledOnce();
    registry.factories.set("javascript", {});
    grammar.mockImplementationOnce(async () => {
      registry.isResolved.mockReturnValue(true);
      return null;
    });
    await tick();
    await ready;
    failOld(new Error("retired grammar failed"));
    await tick();
    expect(grammar).toHaveBeenCalledTimes(2);
    f.owner.dispose();
  });
});
