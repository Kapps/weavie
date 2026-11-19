import { afterEach, expect, it, vi } from "vitest";

const bridge = vi.hoisted(() => ({ request: vi.fn(), changed: () => {} }));
const options = vi.hoisted(() => ({ locale: "fr", changed: () => {} }));
const tokenization = vi.hoisted(() => ({ load: vi.fn() }));
vi.mock("./spell-tokens", () => ({
  createSpellingTokens: () => ({ read: tokenization.load, dispose: () => {} }),
}));
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));
vi.mock("../editor-options", () => ({
  currentEditorOptions: () => ({ spellCheck: true, spellCheckLocale: options.locale }),
  onEditorOptionsChanged: (handler: () => void) => {
    options.changed = handler;
    return () => {};
  },
}));
vi.mock("../commands/registry", () => ({ findCommand: () => ({ keys: ["ctrl+shift+f8"] }) }));
vi.mock("../notify/notify", () => ({ notify: vi.fn() }));
vi.mock("./session-uri-owner", () => ({
  SESSION_FILE_SCHEME: "session",
  sessionForUri: () => ({ feature: () => ({ request: bridge.request }) }),
}));
vi.mock("./monaco-setup", () => ({
  monaco: {
    Range: class {
      constructor(
        public startLineNumber: number,
        public startColumn: number,
        public endLineNumber: number,
        public endColumn: number,
      ) {}
    },
    editor: {
      TrackedRangeStickiness: { NeverGrowsWhenTypingAtEdges: 1 },
    },
  },
}));

import { notify } from "../notify/notify";
import type { monaco } from "./monaco-setup";
import { createSpellCheck } from "./spell-check";

function fixture(text: string) {
  vi.useFakeTimers();
  bridge.request.mockReset();
  tokenization.load.mockReset().mockResolvedValue([]);
  const set = vi.fn();
  const clear = vi.fn();
  let version = 1;
  let visible = true;
  const handlers: Array<(event: unknown) => void> = [];
  const subscribe = (handler: (event: unknown) => void) => {
    handlers.push(handler);
    return { dispose: vi.fn() };
  };
  const model = {
    uri: { scheme: "session" },
    getVersionId: () => version,
    isDisposed: () => false,
    getLanguageId: () => "plaintext",
    getLineContent: () => text,
  };
  const editor = {
    createDecorationsCollection: () => ({ set, clear }),
    getModel: () => model,
    getVisibleRanges: () => (visible ? [{ startLineNumber: 1, endLineNumber: 1 }] : []),
    onDidChangeModel: subscribe,
    onDidChangeModelContent: subscribe,
    onDidChangeModelLanguage: subscribe,
    onDidChangeModelTokens: subscribe,
    onDidScrollChange: subscribe,
    onDidLayoutChange: subscribe,
  };
  const spelling = createSpellCheck(editor as unknown as monaco.editor.IStandaloneCodeEditor);
  return {
    spelling,
    set,
    clear,
    scroll: () => handlers[4]!(undefined),
    tokenize: () => handlers[3]!(undefined),
    switchModel: () => handlers[0]!(undefined),
    emptyViewport: () => {
      visible = false;
    },
    edit: () => {
      version++;
      handlers[1]!({ changes: [], isFlush: false });
    },
  };
}

afterEach(() => vi.useRealTimers());

it("rejects obsolete responses after editing and disposal even when transport completes them", async () => {
  const { spelling, set, edit } = fixture("teh");
  let resolveOld: (value: unknown) => void = () => {};
  bridge.request.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolveOld = resolve;
      }),
  );
  await vi.advanceTimersByTimeAsync(100);
  const oldSignal = bridge.request.mock.calls[0]![2] as AbortSignal;
  edit();
  set.mockClear();
  expect(oldSignal.aborted).toBe(true);
  resolveOld([{ line: 1, offset: 0, word: "teh" }]);
  await Promise.resolve();
  expect(set).not.toHaveBeenCalled();

  let resolveNew: (value: unknown) => void = () => {};
  bridge.request.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolveNew = resolve;
      }),
  );
  await vi.advanceTimersByTimeAsync(100);
  spelling.dispose();
  resolveNew([{ line: 1, offset: 0, word: "teh" }]);
  await Promise.resolve();
  expect(set).not.toHaveBeenCalled();
});

it("sends a visible line whole", async () => {
  const { spelling } = fixture("teh word");
  bridge.request.mockResolvedValue([]);
  await vi.advanceTimersByTimeAsync(100);
  expect(bridge.request.mock.calls[0]![1]).toEqual({
    spans: [{ line: 1, offset: 0, text: "teh word", identifier: false }],
    locale: "fr",
  });
  spelling.dispose();
});

it("drops a check answered after the locale changed and asks again in the new one", async () => {
  const { spelling, set } = fixture("teh");
  let resolveOld: (value: unknown) => void = () => {};
  bridge.request.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolveOld = resolve;
      }),
  );
  await vi.advanceTimersByTimeAsync(100);
  options.locale = "de";
  options.changed();
  set.mockClear();
  resolveOld([{ line: 1, offset: 0, word: "teh" }]);
  await Promise.resolve();
  expect(set).not.toHaveBeenCalled();
  bridge.request.mockResolvedValue([]);
  await vi.advanceTimersByTimeAsync(100);
  expect(bridge.request.mock.calls[1]![1]).toMatchObject({ locale: "de" });
  options.locale = "fr";
  spelling.dispose();
});

it("does not send a check for a model edited while its grammar was loading", async () => {
  const { spelling, edit, set } = fixture("teh");
  let ready: (value: []) => void = () => {};
  tokenization.load.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        ready = resolve;
      }),
  );
  await vi.advanceTimersByTimeAsync(100);
  edit();
  set.mockClear();
  ready([]);
  await Promise.resolve();
  expect(bridge.request).not.toHaveBeenCalled();
  expect(set).not.toHaveBeenCalled();
  spelling.dispose();
});

it("does not check a line over 1,000 characters", async () => {
  const { spelling } = fixture("word ".repeat(201));
  await vi.advanceTimersByTimeAsync(100);
  expect(bridge.request).not.toHaveBeenCalled();
  spelling.dispose();
});

it("retains markers during scroll and token refreshes and replaces them after 100ms idle", async () => {
  const { spelling, set, clear, scroll, tokenize } = fixture("teh");
  bridge.request.mockResolvedValue([{ line: 1, offset: 0, word: "teh" }]);
  await vi.advanceTimersByTimeAsync(99);
  expect(bridge.request).not.toHaveBeenCalled();
  await vi.advanceTimersByTimeAsync(1);
  expect(set.mock.lastCall![0]).toHaveLength(1);
  set.mockClear();
  clear.mockClear();
  scroll();
  await vi.advanceTimersByTimeAsync(50);
  tokenize();
  await vi.advanceTimersByTimeAsync(99);
  expect(set).not.toHaveBeenCalled();
  expect(clear).not.toHaveBeenCalled();
  bridge.request.mockResolvedValue([]);
  await vi.advanceTimersByTimeAsync(1);
  expect(set).toHaveBeenCalledExactlyOnceWith([]);
  spelling.dispose();
});

it("clears retained markers for an empty viewport and on model changes", async () => {
  const { spelling, set, scroll, emptyViewport, switchModel } = fixture("teh");
  bridge.request.mockResolvedValue([{ line: 1, offset: 0, word: "teh" }]);
  await vi.advanceTimersByTimeAsync(100);
  emptyViewport();
  scroll();
  await vi.advanceTimersByTimeAsync(100);
  expect(set.mock.lastCall).toEqual([[]]);
  expect(bridge.request).toHaveBeenCalledTimes(1);
  set.mockClear();
  switchModel();
  expect(set).toHaveBeenCalledExactlyOnceWith([]);
  spelling.dispose();
});

it("keeps markers without an error when the language server cancels its token request", async () => {
  const { spelling, set, scroll } = fixture("teh");
  bridge.request.mockResolvedValue([{ line: 1, offset: 0, word: "teh" }]);
  await vi.advanceTimersByTimeAsync(100);
  set.mockClear();
  vi.mocked(notify).mockClear();
  tokenization.load.mockRejectedValueOnce(
    Object.assign(new Error("Canceled"), { name: "Canceled" }),
  );
  scroll();
  await vi.advanceTimersByTimeAsync(100);
  expect(notify).not.toHaveBeenCalled();
  expect(set).not.toHaveBeenCalled();
  tokenization.load.mockRejectedValueOnce(new Error("boom"));
  scroll();
  await vi.advanceTimersByTimeAsync(100);
  expect(notify).toHaveBeenCalledExactlyOnceWith(
    "error",
    "Spell check failed: Error: boom",
    "spell-check",
  );
  spelling.dispose();
});
