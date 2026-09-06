import { afterEach, expect, it, vi } from "vitest";

const bridge = vi.hoisted(() => ({ request: vi.fn(), changed: () => {} }));
const tokenization = vi.hoisted(() => ({ load: vi.fn() }));
vi.mock("./spell-tokens", () => ({
  spellingTokens: tokenization.load,
}));
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));
vi.mock("../editor-options", () => ({
  currentEditorOptions: () => ({ spellCheck: true }),
  onEditorOptionsChanged: () => () => {},
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
      EditorOption: { wrappingInfo: 1 },
    },
  },
}));

import type { monaco } from "./monaco-setup";
import { createSpellCheck } from "./spell-check";

function fixture(text: string, start: number, end: number) {
  vi.useFakeTimers();
  bridge.request.mockReset();
  tokenization.load.mockReset().mockResolvedValue([]);
  const set = vi.fn();
  const clear = vi.fn();
  let version = 1;
  const handlers: Array<() => void> = [];
  const subscribe = (handler: () => void) => {
    handlers.push(handler);
    return { dispose: vi.fn() };
  };
  const model = {
    uri: { scheme: "session" },
    getVersionId: () => version,
    isDisposed: () => false,
    getLanguageId: () => "plaintext",
    getLineMaxColumn: () => text.length + 1,
    getValueInRange: (range: monaco.IRange) =>
      text.slice(range.startColumn - 1, range.endColumn - 1),
  };
  const editor = {
    createDecorationsCollection: () => ({ set, clear }),
    getModel: () => model,
    getOption: () => ({ wrappingColumn: 80 }),
    getLayoutInfo: () => ({}),
    getVisibleRanges: () => [
      { startLineNumber: 1, endLineNumber: 1, startColumn: start, endColumn: end },
    ],
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
    edit: () => {
      version++;
      handlers[1]!();
    },
  };
}

afterEach(() => vi.useRealTimers());

it("rejects obsolete responses after editing and disposal even when transport completes them", async () => {
  const { spelling, set, edit } = fixture("teh", 1, 4);
  let resolveOld: (value: unknown) => void = () => {};
  bridge.request.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolveOld = resolve;
      }),
  );
  await vi.advanceTimersByTimeAsync(250);
  const oldSignal = bridge.request.mock.calls[0]![2] as AbortSignal;
  edit();
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
  await vi.advanceTimersByTimeAsync(250);
  spelling.dispose();
  resolveNew([{ line: 1, offset: 0, word: "teh" }]);
  await Promise.resolve();
  expect(set).not.toHaveBeenCalled();
});

it("sends fully visible words without scanning offscreen portions of a wrapped line", async () => {
  const text = `${"before ".repeat(10000)}edge visible edge ${"after ".repeat(10000)}`;
  const { spelling } = fixture(text, 70002, 70016);
  bridge.request.mockResolvedValue([]);
  await vi.advanceTimersByTimeAsync(250);
  expect(bridge.request.mock.calls[0]![1]).toEqual({
    spans: [{ line: 1, offset: 70004, text: " visible ", identifier: false }],
  });
  spelling.dispose();
});

it("does not send a check for a model edited while its grammar was loading", async () => {
  const { spelling, edit, set } = fixture("teh", 1, 4);
  let ready: (value: []) => void = () => {};
  tokenization.load.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        ready = resolve;
      }),
  );
  await vi.advanceTimersByTimeAsync(250);
  edit();
  ready([]);
  await Promise.resolve();
  expect(bridge.request).not.toHaveBeenCalled();
  expect(set).not.toHaveBeenCalled();
  spelling.dispose();
});

it("does not check the offscreen remainder of a long unbroken token", async () => {
  const { spelling } = fixture("a".repeat(1000000), 40001, 40101);
  await vi.advanceTimersByTimeAsync(250);
  expect(bridge.request).not.toHaveBeenCalled();
  spelling.dispose();
});
