import { beforeEach, describe, expect, it, vi } from "vitest";

const computation = vi.hoisted(() => ({ compute: vi.fn(), dispose: vi.fn() }));
vi.mock("@codingame/monaco-vscode-api", () => ({}));
vi.mock("./diff-computer", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./diff-computer")>()),
  DiffComputer: class {
    compute = computation.compute;
    dispose = computation.dispose;
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

import type { monaco } from "../monaco-setup";
import { ReviewDocument, ReviewDocumentScope } from "./review-document";
import type { ReviewActionPresentation } from "./review-file-actions";

const sources = { original: "before", claudeVersion: "after", acceptedBaseline: undefined };
const configuration = { mode: "applied" as const, original: "before", claudeVersion: "after" };
const ready = { status: "ready", changes: [], userChanges: [], fadedChanges: [] };

function modelFixture() {
  let version = 1;
  let disposed = false;
  const listeners = new Set<() => void>();
  const model = {
    uri: { toString: () => "weavie-file:/same-path" },
    getVersionId: () => version,
    getLineCount: () => 3,
    isDisposed: () => disposed,
    onWillDispose: (callback: () => void) => {
      listeners.add(callback);
      return { dispose: () => listeners.delete(callback) };
    },
  } as unknown as monaco.editor.ITextModel;
  return {
    model,
    listeners,
    edit: () => version++,
    dispose: () => {
      disposed = true;
      for (const listener of listeners) listener();
    },
  };
}

describe("model-owned review documents", () => {
  beforeEach(() => {
    computation.compute.mockReset().mockReturnValue(ready);
    computation.dispose.mockReset();
  });

  it("shares one in-flight calculation and synchronous prepared geometry between paint adapters", async () => {
    const { model } = modelFixture();
    const document = new ReviewDocument(model, () => undefined);
    let finish!: (result: unknown) => void;
    computation.compute.mockReturnValue(new Promise((resolve) => (finish = resolve)));
    const passive = document.prepare(sources);
    const active = document.prepare({ ...sources });
    expect(active).toBe(passive);
    expect(computation.compute).toHaveBeenCalledOnce();
    expect(computation.dispose).toHaveBeenCalledOnce();
    finish(ready);
    const geometry = await passive;
    expect(document.prepare(sources)).toBe(geometry);
    expect(geometry).toMatchObject({ status: "ready", version: 1, model });
  });

  it.each([
    "model",
    "sources",
    "disposed",
  ])("rejects an old worker completion when the %s owner changes", async (change) => {
    const fixture = modelFixture();
    const document = new ReviewDocument(fixture.model, () => undefined);
    let finish!: (result: unknown) => void;
    computation.compute.mockReturnValueOnce(new Promise((resolve) => (finish = resolve)));
    const pending = document.prepare(sources);
    const rejection = expect(pending).rejects.toMatchObject({ name: "AbortError" });
    if (change === "model") fixture.edit();
    if (change === "sources") document.prepare({ ...sources, original: "different" });
    if (change === "disposed") document.dispose();
    finish(ready);
    await rejection;
  });

  it("does not reuse geometry after an edit or a host source change", () => {
    const fixture = modelFixture();
    const document = new ReviewDocument(fixture.model, () => undefined);
    const original = document.prepare(sources);
    fixture.edit();
    const edited = document.prepare(sources);
    const rebased = document.prepare({ ...sources, acceptedBaseline: "older" });
    expect(edited).not.toBe(original);
    expect(rebased).not.toBe(edited);
    expect(edited).toMatchObject({ version: 2 });
    expect(computation.compute).toHaveBeenCalledTimes(3);
  });

  it("allows a failed calculation to be retried without publishing it as geometry", () => {
    const document = new ReviewDocument(modelFixture().model, () => undefined);
    const error = new Error("worker failed");
    computation.compute.mockReturnValueOnce({ status: "failed", error });
    expect(document.prepare(sources)).toEqual({ status: "failed", error });
    expect(document.prepare(sources)).toMatchObject({ status: "ready" });
    expect(computation.compute).toHaveBeenCalledTimes(2);
  });

  it("does not let a pending source revision overwrite a restored cached revision", async () => {
    const document = new ReviewDocument(modelFixture().model, () => undefined);
    const original = document.prepare(sources);
    let finish!: (result: unknown) => void;
    computation.compute.mockReturnValueOnce(new Promise((resolve) => (finish = resolve)));
    const pending = document.prepare({ ...sources, original: "new baseline" });
    const rejected = expect(pending).rejects.toMatchObject({ name: "AbortError" });
    expect(document.prepare(sources)).toBe(original);
    finish(ready);
    await rejected;
    expect(document.prepare(sources)).toBe(original);
  });

  it("owns a copy of sources while their worker request is in flight", async () => {
    const document = new ReviewDocument(modelFixture().model, () => undefined);
    const input = { ...sources };
    let finish!: (result: unknown) => void;
    computation.compute.mockReturnValueOnce(new Promise((resolve) => (finish = resolve)));
    const pending = document.prepare(input);
    input.original = "mutated";
    finish(ready);
    expect(await pending).toMatchObject({ sources });
  });

  it("retains incremental worker sources only while active adapters hold a lease", () => {
    const fixture = modelFixture();
    const document = new ReviewDocument(fixture.model, () => undefined);
    const first = document.retainSources();
    const second = document.retainSources();
    document.prepare(sources);
    fixture.edit();
    document.prepare(sources);
    expect(computation.dispose).not.toHaveBeenCalled();
    first.dispose();
    first.dispose();
    expect(computation.dispose).not.toHaveBeenCalled();
    second.dispose();
    expect(computation.dispose).toHaveBeenCalledOnce();
    fixture.edit();
    document.prepare(sources);
    expect(computation.dispose).toHaveBeenCalledTimes(2);
  });

  it("prunes completed review documents without disposing open working models", () => {
    const fixture = modelFixture();
    const scope = new ReviewDocumentScope();
    const previous = scope.forModel(fixture.model);
    previous.prepare(sources);
    scope.retain(() => false);
    expect(fixture.listeners.size).toBe(0);
    expect(fixture.model.isDisposed()).toBe(false);
    expect(() => previous.prepare(sources)).toThrow("closed");
    const next = scope.forModel(fixture.model);
    expect(next).not.toBe(previous);
    expect(next.prepare(sources)).toMatchObject({ status: "ready" });
    scope.dispose();
  });

  it("keys ownership by exact model rather than URI and releases model subscriptions", () => {
    const a = modelFixture();
    const b = modelFixture();
    const scope = new ReviewDocumentScope();
    const first = scope.forModel(a.model);
    const replacement = scope.forModel(b.model);
    expect(first).not.toBe(replacement);
    expect(scope.forModel(a.model)).toBe(first);
    a.dispose();
    expect(a.listeners.size).toBe(0);
    expect(() => first.prepare(sources)).toThrow("closed");
    expect(replacement.prepare(sources)).toMatchObject({ status: "ready" });
    scope.dispose();
    expect(b.listeners.size).toBe(0);
    expect(b.model.isDisposed()).toBe(false);
    expect(() => replacement.prepare(sources)).toThrow("closed");
    expect(() => scope.forModel(b.model)).toThrow("closed");
  });

  it("does not share actions between independent presentation scopes of the same model", () => {
    const { model } = modelFixture();
    const left = new ReviewDocumentScope();
    const right = new ReviewDocumentScope();
    const a = left.forModel(model);
    const b = right.forModel(model);
    const leftNext = vi.fn();
    const rightNext = vi.fn();
    left.configure(model.uri.toString(), {
      mode: "applied",
      original: "left",
      onNextFile: leftNext,
    });
    right.configure(model.uri.toString(), {
      mode: "applied",
      original: "right",
      onNextFile: rightNext,
    });
    expect(a.actions).not.toBe(b.actions);
    expect(a.actions.options?.onNextFile).toBe(leftNext);
    expect(b.actions.options?.onNextFile).toBe(rightNext);
    left.dispose();
    expect(a.actions.options).toBeUndefined();
    expect(b.actions.options?.onNextFile).toBe(rightNext);
    right.dispose();
  });

  it("adapter source leases do not own the file's action configuration", () => {
    const { model } = modelFixture();
    const scope = new ReviewDocumentScope();
    const document = scope.forModel(model);
    scope.configure(model.uri.toString(), { mode: "applied", original: "before" });
    const actions = document.actions;
    const configured = actions.options;
    const lease = document.retainSources();
    document.prepare(sources);
    lease.dispose();
    expect(document.actions).toBe(actions);
    expect(actions.options).toBe(configured);
    expect(actions.geometry?.version).toBe(1);
    scope.dispose();
    expect(actions.options).toBeUndefined();
  });

  it("a valid late worker completion cannot restore superseded action options", async () => {
    const { model } = modelFixture();
    const scope = new ReviewDocumentScope();
    const document = scope.forModel(model);
    scope.configure(model.uri.toString(), {
      mode: "applied",
      original: "before",
      claudeVersion: "after",
    });
    let finish!: (result: unknown) => void;
    computation.compute.mockReturnValueOnce(new Promise((resolve) => (finish = resolve)));
    const pending = document.prepare(sources);
    scope.configure(model.uri.toString(), {
      mode: "applied",
      original: "new baseline",
      claudeVersion: "after",
    });
    const configured = document.actions.options;
    finish(ready);
    await pending;
    expect(document.actions.options).toBe(configured);
    expect(document.actions.stale).toBe(true);
    scope.dispose();
  });

  it("reuses geometry across same-source callback changes without reusing action identity", () => {
    const { model } = modelFixture();
    const scope = new ReviewDocumentScope();
    const document = scope.forModel(model);
    const before = {
      mode: "applied" as const,
      original: "before",
      claudeVersion: "after",
      onNextFile: vi.fn(),
    };
    scope.configure(model.uri.toString(), before);
    const firstOptions = document.actions.options;
    const geometry = document.prepare(sources);
    scope.configure(model.uri.toString(), { ...before, onNextFile: vi.fn() });
    expect(document.prepare(sources)).toBe(geometry);
    expect(document.actions.options).not.toBe(firstOptions);
    expect(document.actions.stale).toBe(false);
    expect(computation.compute).toHaveBeenCalledOnce();
    scope.dispose();
  });

  it("owns one immutable configuration before and across exact-model bindings", () => {
    const first = modelFixture();
    const second = modelFixture();
    const scope = new ReviewDocumentScope();
    const uri = first.model.uri.toString();
    const options = { mode: "applied" as const, original: "before", onKeepFile: vi.fn() };
    scope.configure(uri, options);
    const configured = scope.get(uri);
    options.original = "mutated";
    const a = scope.forModel(first.model);
    const b = scope.forModel(second.model);
    expect(configured?.original).toBe("before");
    expect(Object.isFrozen(configured)).toBe(true);
    expect(a.actions.options).toBe(configured);
    expect(b.actions.options).toBe(configured);
    first.dispose();
    expect(a.actions.options).toBeUndefined();
    expect(b.actions.options).toBe(configured);
    expect(scope.get(uri)).toBe(configured);
    scope.dispose();
  });

  it("never resurrects captures or old documents through A→B→A or clear/reconfigure", () => {
    const { model } = modelFixture();
    const scope = new ReviewDocumentScope();
    const uri = model.uri.toString();
    const options = { ...configuration, onNextFile: vi.fn() };
    const port: ReviewActionPresentation = {
      scope: { current: "file" },
      valid: () => true,
      availability: () => "unavailable",
      reviewLine: () => 1,
      commentLine: () => 1,
      revealLine: vi.fn(),
      selectLine: vi.fn(),
      openComment: vi.fn(),
      composerFocused: () => false,
      swallowFileNavigation: false,
    };
    scope.configure(uri, options);
    const old = scope.forModel(model);
    old.prepare(sources);
    const captured = old.actions.capture(port);
    scope.configure(uri, { ...options, onNextFile: vi.fn() });
    scope.configure(uri, options);
    expect(() => captured.nextFile()).toThrow("location");
    const commands = old.actions.commands(port);
    expect(commands.nextFile()).toBe(true);
    scope.configure(uri, undefined);
    scope.configure(uri, options);
    const replacement = scope.forModel(model);
    expect(replacement).not.toBe(old);
    expect(old.actions.options).toBeUndefined();
    expect(commands.nextFile()).toBe(false);
    expect(() => old.prepare(sources)).toThrow("closed");
    expect(() => old.retainSources()).toThrow("closed");
    expect(replacement.actions.commands(port).nextFile()).toBe(true);
    expect(options.onNextFile).toHaveBeenCalledTimes(2);
    scope.dispose();
  });

  it("rejects a late worker completion after clear without changing the replacement", async () => {
    const { model } = modelFixture();
    const scope = new ReviewDocumentScope();
    const uri = model.uri.toString();
    scope.configure(uri, configuration);
    const old = scope.forModel(model);
    let finish!: (result: unknown) => void;
    computation.compute.mockReturnValueOnce(new Promise((resolve) => (finish = resolve)));
    const rejected = expect(old.prepare(sources)).rejects.toMatchObject({ name: "AbortError" });
    scope.configure(uri, undefined);
    scope.configure(uri, configuration);
    const current = scope.forModel(model);
    const geometry = current.prepare(sources);
    finish(ready);
    await rejected;
    expect(current.prepare(sources)).toBe(geometry);
    expect(old.actions.options).toBeUndefined();
    scope.dispose();
  });

  it("preserves a replacement created reentrantly while an old document is disposed", () => {
    const { model } = modelFixture();
    const scope = new ReviewDocumentScope();
    const uri = model.uri.toString();
    scope.configure(uri, configuration);
    const old = scope.forModel(model);
    let replacement: ReviewDocument | undefined;
    computation.dispose.mockImplementationOnce(() => {
      scope.configure(uri, configuration);
      replacement = scope.forModel(model);
    });
    scope.configure(uri, undefined);
    expect(replacement).toBeDefined();
    expect(replacement).not.toBe(old);
    expect(scope.forModel(model)).toBe(replacement);
    expect(replacement!.prepare(sources)).toMatchObject({ status: "ready" });
    scope.dispose();
  });

  it("notifies with current registry state and keeps URI observers through clear", () => {
    const { model } = modelFixture();
    const scope = new ReviewDocumentScope();
    const uri = model.uri.toString();
    const seen: (string | undefined)[] = [];
    scope.onDidChangeConfiguration((key) => {
      if (scope.get(key)?.original === "A")
        scope.configure(key, { mode: "applied", original: "B" });
    });
    scope.onDidChangeConfiguration((key) => seen.push(scope.get(key)?.original));
    scope.configure(uri, { mode: "applied", original: "A" });
    expect(seen.length).toBeGreaterThan(0);
    expect(seen.every((value) => value === "B")).toBe(true);
    scope.configure(uri, undefined);
    expect(seen.at(-1)).toBeUndefined();
    scope.configure(uri, { mode: "applied", original: "C" });
    expect(seen.at(-1)).toBe("C");
    scope.dispose();
    expect(scope.has(uri)).toBe(false);
    expect(() => scope.configure(uri, configuration)).toThrow("closed");
  });
});
