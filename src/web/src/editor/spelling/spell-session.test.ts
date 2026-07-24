import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  post: vi.fn(),
  settings: { enabled: true, locale: "en-US" },
  settingsHandler: undefined as
    | ((settings: { enabled: boolean; locale: string }) => void)
    | undefined,
  binding: {
    backendId: "remote-a",
    protocol: "projection" as const,
    railSessionId: "rail-a",
    sessionId: "session-a",
    projectionEpoch: "epoch-a",
    projectionRevision: 7,
    projectionPageId: "page-a",
  },
}));

vi.mock("../../bridge", () => ({
  currentEditorBinding: () => mocks.binding,
  editorAttribution: () => ({
    sessionId: mocks.binding.sessionId,
    projectionEpoch: mocks.binding.projectionEpoch,
    projectionRevision: mocks.binding.projectionRevision,
    projectionPageId: mocks.binding.projectionPageId,
  }),
  postToEditorBinding: mocks.post,
}));

vi.mock("../../notify/notify", () => ({ notify: vi.fn() }));

vi.mock("../../spell-settings", () => ({
  currentSpellSettings: () => mocks.settings,
  onSpellSettingsChanged: (handler: (settings: { enabled: boolean; locale: string }) => void) => {
    mocks.settingsHandler = handler;
    return () => {
      if (mocks.settingsHandler === handler) {
        mocks.settingsHandler = undefined;
      }
    };
  },
}));

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

import { SpellSession } from "./spell-session";

interface FakeModel {
  id: string;
  uri: { authority: string; path: string };
  onWillDispose(callback: () => void): { dispose(): void };
  getValue(): string;
  getVersionId(): number;
  getLineCount(): number;
  getLineContent(line: number): string;
  getLineMaxColumn(line: number): number;
  getValueInRange(range: {
    startLineNumber: number;
    startColumn: number;
    endColumn: number;
  }): string;
}

function createHarness() {
  let content = "teh";
  let version = 4;
  const setDecorations = vi.fn();
  const model: FakeModel = {
    id: "model-a",
    uri: { authority: "", path: "/repo/file.md" },
    onWillDispose: () => ({ dispose: vi.fn() }),
    getValue: () => content,
    getVersionId: () => version,
    getLineCount: () => 1,
    getLineContent: () => content,
    getLineMaxColumn: () => content.length + 1,
    getValueInRange: (range) => content.slice(range.startColumn - 1, range.endColumn - 1),
  };
  const editor = {
    createDecorationsCollection: () => ({ set: setDecorations, clear: vi.fn() }),
    onDidChangeModel: () => ({ dispose: vi.fn() }),
    getModel: () => model,
  };
  return {
    model,
    editor,
    setDecorations,
    change(nextContent: string, nextVersion: number) {
      content = nextContent;
      version = nextVersion;
    },
  };
}

function changeSettings(enabled: boolean, locale = "en-US"): void {
  mocks.settings = { enabled, locale };
  mocks.settingsHandler?.(mocks.settings);
}

function diagnostics(documentRevision: number) {
  return {
    type: "spell-diagnostics" as const,
    path: "/repo/file.md",
    documentRevision,
    locale: "en-US",
    issues: [
      {
        line: 1,
        startColumn: 1,
        endColumn: 4,
        word: "teh",
      },
    ],
    sessionId: "session-a",
    projectionEpoch: "epoch-a",
    projectionRevision: 7,
    projectionPageId: "page-a",
  };
}

describe("SpellSession", () => {
  beforeEach(() => {
    mocks.post.mockReset();
    mocks.settings = { enabled: true, locale: "en-US" };
    mocks.settingsHandler = undefined;
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("debounces open and changed whole documents with browser-owned revisions", () => {
    vi.useFakeTimers();
    const harness = createHarness();
    const session = new SpellSession(harness.editor as never);
    session.track(harness.model as never);

    vi.advanceTimersByTime(149);
    expect(mocks.post).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(mocks.post).toHaveBeenLastCalledWith(
      mocks.binding,
      expect.objectContaining({
        type: "spell-document-changed",
        content: "teh",
        documentRevision: 1,
      }),
    );

    harness.change("clean host reload", 5);
    session.contentChanged(harness.model as never);
    vi.advanceTimersByTime(149);
    expect(mocks.post).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1);
    expect(mocks.post).toHaveBeenLastCalledWith(
      mocks.binding,
      expect.objectContaining({
        type: "spell-document-changed",
        path: "/repo/file.md",
        content: "clean host reload",
        documentRevision: 2,
      }),
    );

    harness.change("user edit", 6);
    session.contentChanged(harness.model as never);
    vi.advanceTimersByTime(150);
    expect(mocks.post).toHaveBeenLastCalledWith(
      mocks.binding,
      expect.objectContaining({
        content: "user edit",
        documentRevision: 3,
      }),
    );
    session.dispose();
  });

  it("clears and resubmits tracked models after settings or dictionary changes", () => {
    vi.useFakeTimers();
    const harness = createHarness();
    const session = new SpellSession(harness.editor as never);
    session.track(harness.model as never);
    vi.advanceTimersByTime(150);
    session.handleDiagnostics(diagnostics(1));
    expect(harness.setDecorations).toHaveBeenLastCalledWith([expect.any(Object)]);

    changeSettings(false);
    expect(harness.setDecorations).toHaveBeenLastCalledWith([]);
    vi.advanceTimersByTime(150);
    expect(mocks.post).toHaveBeenCalledTimes(1);

    changeSettings(true);
    vi.advanceTimersByTime(150);
    expect(mocks.post).toHaveBeenLastCalledWith(
      mocks.binding,
      expect.objectContaining({ documentRevision: 2 }),
    );

    session.handleDiagnostics(diagnostics(2));
    expect(harness.setDecorations).toHaveBeenLastCalledWith([expect.any(Object)]);
    session.handleDictionaryChanged({
      type: "spell-dictionary-changed",
      sessionId: "session-a",
      projectionEpoch: "epoch-a",
      projectionRevision: 7,
      projectionPageId: "page-a",
    });
    expect(harness.setDecorations).toHaveBeenLastCalledWith([]);
    vi.advanceTimersByTime(150);
    expect(mocks.post).toHaveBeenLastCalledWith(
      mocks.binding,
      expect.objectContaining({ documentRevision: 3 }),
    );
    session.dispose();
  });

  it("rejects a superseded suggestion request before sending the newer one", async () => {
    vi.useFakeTimers();
    const harness = createHarness();
    const session = new SpellSession(harness.editor as never);
    session.track(harness.model as never);
    vi.advanceTimersByTime(150);
    session.handleDiagnostics(diagnostics(1));
    const context = { ...diagnostics(1).issues[0]!, modelId: harness.model.id };
    expect(session.isCurrentContext(context)).toBe(true);

    const firstResult = session.requestSuggestions(context).catch((error: unknown) => error);
    const second = session.requestSuggestions(context);

    await expect(firstResult).resolves.toEqual(
      new Error("A newer spelling suggestion request replaced this one."),
    );
    expect(mocks.post).toHaveBeenLastCalledWith(
      mocks.binding,
      expect.objectContaining({
        type: "spell-suggest",
        requestId: "spell-suggest-2",
        word: "teh",
      }),
    );

    harness.change("the", 5);
    session.contentChanged(harness.model as never);
    expect(session.isCurrentContext(context)).toBe(false);
    const secondRejection = expect(second).rejects.toThrow("The editor was disposed.");
    session.dispose();
    await secondRejection;
  });
});
