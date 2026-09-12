import type {
  DocumentRangeSemanticTokensProvider,
  DocumentSemanticTokensProvider,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/languages";
import { afterEach, expect, it, vi } from "vitest";
import type { monaco } from "./monaco-setup";

const services = vi.hoisted(() => ({ current: {} }));
vi.mock("@codingame/monaco-vscode-api", () => ({
  StandaloneServices: { get: () => services.current },
}));

import { createSpellingTokens } from "./spell-tokens";

function event() {
  const listeners = new Set<() => void>();
  return {
    subscribe: (listener: () => void) => {
      listeners.add(listener);
      return { dispose: () => listeners.delete(listener) };
    },
    fire: () => {
      for (const listener of listeners) listener();
    },
    listeners,
  };
}
const disposables: Array<{ dispose(): void }> = [];
afterEach(() => {
  for (const disposable of disposables.splice(0)) disposable.dispose();
});

function fixture() {
  const content = event();
  const language = event();
  const disposed = event();
  const registered = event();
  const refreshed = event();
  const provide = vi.fn().mockResolvedValue({ resultId: "result", data: new Uint32Array() });
  const release = vi.fn();
  const provider = {
    getLegend: () => ({
      tokenTypes: ["variable", "parameter"],
      tokenModifiers: ["readonly", "declaration", "definition"],
    }),
    provideDocumentSemanticTokens: provide,
    releaseDocumentSemanticTokens: release,
    onDidChange: refreshed.subscribe,
  } as DocumentSemanticTokensProvider;
  let providers = [provider];
  let rangeProviders: DocumentRangeSemanticTokensProvider[] = [];
  services.current = {
    documentSemanticTokensProvider: {
      orderedGroups: () => (providers.length === 0 ? [] : [providers]),
      onDidChange: registered.subscribe,
    },
    documentRangeSemanticTokensProvider: {
      orderedGroups: () => (rangeProviders.length === 0 ? [] : [rangeProviders]),
      onDidChange: event().subscribe,
    },
  };
  const model = {
    onDidChangeContent: content.subscribe,
    onDidChangeLanguage: language.subscribe,
    onWillDispose: disposed.subscribe,
    getFullModelRange: () => ({
      startLineNumber: 1,
      startColumn: 1,
      endLineNumber: 9,
      endColumn: 80,
    }),
  } as unknown as monaco.editor.ITextModel;
  const changed = vi.fn();
  const source = createSpellingTokens(changed);
  disposables.push(source);
  const ranges = [{ line: 1, offset: 0, text: " ".repeat(80), identifier: false }];
  const signal = new AbortController().signal;
  return {
    source,
    model,
    ranges,
    signal,
    provide,
    release,
    provider,
    content,
    disposed,
    registered,
    refreshed,
    changed,
    setRangeProviders: (next: DocumentRangeSemanticTokensProvider[]) => {
      rangeProviders = next;
    },
    setProviders: (next: DocumentSemanticTokensProvider[]) => {
      providers = next;
    },
  };
}

it("checks declaration and definition metadata, including parameters, but excludes uses", async () => {
  const f = fixture();
  f.provide.mockResolvedValue({
    resultId: "one",
    data: new Uint32Array([0, 6, 9, 0, 2, 0, 12, 9, 0, 0, 0, 12, 8, 1, 4, 1, 3, 6, 0, 2]),
  });
  expect(await f.source.read(f.model, f.ranges, f.signal)).toEqual([
    { line: 1, startIndex: 6, endIndex: 15 },
    { line: 1, startIndex: 30, endIndex: 38 },
  ]);
  expect(f.release).toHaveBeenCalledWith("one");
  expect(f.changed).not.toHaveBeenCalled();
});

it("reuses metadata across viewport changes and invalidates on edits and provider refresh", async () => {
  const f = fixture();
  f.provide.mockResolvedValue({ data: new Uint32Array([0, 6, 9, 0, 2, 1, 3, 6, 0, 2]) });
  await f.source.read(f.model, f.ranges, f.signal);
  expect(
    await f.source.read(
      f.model,
      [{ line: 2, offset: 5, text: "xxxx", identifier: false }],
      f.signal,
    ),
  ).toEqual([{ line: 2, startIndex: 5, endIndex: 9 }]);
  expect(f.provide).toHaveBeenCalledTimes(1);
  f.content.fire();
  await f.source.read(f.model, f.ranges, f.signal);
  f.refreshed.fire();
  await f.source.read(f.model, f.ranges, f.signal);
  expect(f.provide).toHaveBeenCalledTimes(3);
  expect(f.changed).toHaveBeenCalledTimes(1);
});

it("refreshes when a declaration provider becomes available", async () => {
  const f = fixture();
  f.setProviders([]);
  expect(await f.source.read(f.model, f.ranges, f.signal)).toEqual([]);
  f.setProviders([f.provider]);
  f.registered.fire();
  await f.source.read(f.model, f.ranges, f.signal);
  expect(f.provide).toHaveBeenCalledTimes(1);
  expect(f.changed).toHaveBeenCalledTimes(1);
});

it("cancels stale model requests and releases late results", async () => {
  const f = fixture();
  let resolve!: (value: { resultId: string; data: Uint32Array }) => void;
  f.provide.mockImplementation(
    () =>
      new Promise((done) => {
        resolve = done;
      }),
  );
  const result = f.source.read(f.model, f.ranges, f.signal);
  const rejected = expect(result).rejects.toMatchObject({ name: "AbortError" });
  const token = f.provide.mock.calls[0]![2];
  f.content.fire();
  expect(token.isCancellationRequested).toBe(true);
  resolve({ resultId: "late", data: new Uint32Array() });
  await rejected;
  expect(f.release).toHaveBeenCalledWith("late");
});

it("propagates provider failures and retries on a later check", async () => {
  const f = fixture();
  f.provide.mockRejectedValueOnce(new Error("semantic failure"));
  await expect(f.source.read(f.model, f.ranges, f.signal)).rejects.toThrow("semantic failure");
  await f.source.read(f.model, f.ranges, f.signal);
  expect(f.provide).toHaveBeenCalledTimes(2);
});

it("merges provider declarations in source order without duplicates", async () => {
  const f = fixture();
  f.provide.mockResolvedValue({ data: new Uint32Array([0, 30, 8, 0, 2]) });
  f.setProviders([
    f.provider,
    {
      ...f.provider,
      provideDocumentSemanticTokens: async () => ({
        data: new Uint32Array([0, 6, 9, 0, 2, 0, 24, 8, 0, 2]),
      }),
    },
  ]);
  expect(await f.source.read(f.model, f.ranges, f.signal)).toEqual([
    { line: 1, startIndex: 6, endIndex: 15 },
    { line: 1, startIndex: 30, endIndex: 38 },
  ]);
});

it("disposal removes subscriptions and aborts pending metadata", async () => {
  const f = fixture();
  await f.source.read(f.model, f.ranges, f.signal);
  f.source.dispose();
  expect(f.content.listeners.size).toBe(0);
  expect(f.refreshed.listeners.size).toBe(0);
  expect(f.registered.listeners.size).toBe(0);
});

it("uses range-only providers and prefers document providers when both are supported", async () => {
  const f = fixture();
  const provideRange = vi.fn().mockResolvedValue({ data: new Uint32Array([0, 6, 9, 0, 2]) });
  f.setRangeProviders([
    { getLegend: f.provider.getLegend, provideDocumentRangeSemanticTokens: provideRange },
  ]);
  await f.source.read(f.model, f.ranges, f.signal);
  expect(provideRange).not.toHaveBeenCalled();
  f.setProviders([]);
  f.registered.fire();
  expect(await f.source.read(f.model, f.ranges, f.signal)).toEqual([
    { line: 1, startIndex: 6, endIndex: 15 },
  ]);
  expect(provideRange).toHaveBeenCalledTimes(1);
});

it("does not guess declarations when the provider has no declaration metadata", async () => {
  const f = fixture();
  f.setProviders([
    {
      ...f.provider,
      getLegend: () => ({ tokenTypes: ["variable"], tokenModifiers: ["readonly"] }),
    },
  ]);
  f.provide.mockResolvedValue({ data: new Uint32Array([0, 6, 9, 0, 1]) });
  expect(await f.source.read(f.model, f.ranges, f.signal)).toEqual([]);
});
