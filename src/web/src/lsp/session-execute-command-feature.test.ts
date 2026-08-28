import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type RegisteredHandler = (_accessor: unknown, ...args: unknown[]) => unknown;
type ExecuteMiddleware = (
  command: string,
  args: unknown[],
  next: (command: string, args: unknown[]) => unknown,
) => unknown;

const runtime = vi.hoisted(() => ({
  handlers: new Map<string, RegisteredHandler>(),
  registrations: [] as string[],
  disposals: [] as string[],
}));

vi.mock("monaco-editor", () => ({
  editor: {
    registerCommand: (command: string, handler: RegisteredHandler) => {
      if (runtime.handlers.has(command)) {
        throw new Error(`command '${command}' already exists`);
      }
      runtime.handlers.set(command, handler);
      runtime.registrations.push(command);
      return {
        dispose: () => {
          if (runtime.handlers.get(command) === handler) {
            runtime.handlers.delete(command);
            runtime.disposals.push(command);
          }
        },
      };
    },
  },
}));

vi.mock("vscode-languageclient", () => ({
  ExecuteCommandRequest: {
    method: "workspace/executeCommand",
    type: { method: "workspace/executeCommand" },
  },
}));

import {
  SessionCommandScope,
  SessionExecuteCommandFeature,
} from "./session-execute-command-feature";

const COMMAND = "gopls.add_dependency";
const CLIENT_COMMAND = "textDocument/references";
const features: SessionExecuteCommandFeature[] = [];

function createTarget(
  namespace: string,
  middleware: ExecuteMiddleware | undefined,
): {
  scope: SessionCommandScope;
  feature: SessionExecuteCommandFeature;
  requests: unknown[];
} {
  const requests: unknown[] = [];
  const client = {
    middleware: { executeCommand: middleware },
    sendRequest: (_type: unknown, params: unknown) => {
      requests.push(params);
      return Promise.resolve(undefined);
    },
    handleFailedRequest: (
      _type: unknown,
      _token: unknown,
      error: unknown,
      _defaultValue: unknown,
    ) => {
      throw error;
    },
  };
  const scope = new SessionCommandScope(namespace);
  const feature = new SessionExecuteCommandFeature(client as never, scope);
  features.push(feature);
  return { scope, feature, requests };
}

function initialize(target: ReturnType<typeof createTarget>): string {
  target.feature.initialize({ executeCommandProvider: { commands: [COMMAND] } }, undefined);
  return target.scope.converters.protocol2Code(COMMAND);
}

beforeEach(() => {
  runtime.handlers.clear();
  runtime.registrations.length = 0;
  runtime.disposals.length = 0;
  features.length = 0;
});

afterEach(() => {
  for (const feature of features) {
    feature.clear();
  }
});

describe("session-owned LSP execute commands", () => {
  it("carries the producing client in a unique alias", async () => {
    const first = createTarget("lsp1-page", undefined);
    const second = createTarget("lsp2-page", undefined);

    expect(first.scope.converters.protocol2Code(CLIENT_COMMAND)).toBe(CLIENT_COMMAND);
    const firstAlias = initialize(first);
    const secondAlias = initialize(second);

    expect(firstAlias).not.toBe(COMMAND);
    expect(secondAlias).not.toBe(firstAlias);
    expect(runtime.registrations).toEqual([firstAlias, secondAlias]);

    await runtime.handlers.get(firstAlias)?.({}, "example.com/first");
    expect(first.requests).toEqual([{ command: COMMAND, arguments: ["example.com/first"] }]);
    expect(second.requests).toEqual([]);

    await runtime.handlers.get(secondAlias)?.({}, "example.com/second");
    expect(second.requests).toEqual([{ command: COMMAND, arguments: ["example.com/second"] }]);
  });

  it("round-trips aliases through resolve conversion for the client lifetime", () => {
    const target = createTarget("resolve", undefined);
    const alias = initialize(target);

    expect(target.scope.converters.code2Protocol(alias)).toBe(COMMAND);
    expect(target.scope.converters.code2Protocol(CLIENT_COMMAND)).toBe(CLIENT_COMMAND);

    target.feature.clear();
    expect(runtime.handlers.has(alias)).toBe(false);
    expect(target.scope.converters.protocol2Code(COMMAND)).toBe(alias);
    expect(target.scope.converters.code2Protocol(alias)).toBe(COMMAND);

    target.feature.register({ id: "again", registerOptions: { commands: [COMMAND] } });
    expect(target.scope.converters.protocol2Code(COMMAND)).toBe(alias);
    expect(runtime.handlers.has(alias)).toBe(true);
  });

  it("never lets stale reconnect commands reach the replacement", async () => {
    const old = createTarget("old-channel", undefined);
    const replacement = createTarget("new-channel", undefined);
    const oldAlias = initialize(old);
    const replacementAlias = initialize(replacement);

    old.feature.clear();

    expect(oldAlias).not.toBe(replacementAlias);
    expect(runtime.handlers.has(oldAlias)).toBe(false);
    expect(runtime.handlers.has(replacementAlias)).toBe(true);
    await runtime.handlers.get(replacementAlias)?.({});
    expect(old.requests).toEqual([]);
    expect(replacement.requests).toEqual([{ command: COMMAND, arguments: [] }]);
  });

  it("reference-counts overlapping dynamic registrations", () => {
    const target = createTarget("dynamic", undefined);
    target.feature.register({ id: "one", registerOptions: { commands: [COMMAND] } });
    target.feature.register({ id: "two", registerOptions: { commands: [COMMAND] } });
    const alias = target.scope.converters.protocol2Code(COMMAND);

    target.feature.unregister("one");
    expect(runtime.handlers.has(alias)).toBe(true);
    target.feature.unregister("two");
    expect(runtime.handlers.has(alias)).toBe(false);
  });

  it("preserves raw command IDs through middleware and onto the wire", async () => {
    const calls: unknown[] = [];
    const target = createTarget("middleware", (command, args, next) => {
      calls.push({ command, args });
      return next(command, args);
    });
    const alias = initialize(target);

    await runtime.handlers.get(alias)?.({}, "arg");

    expect(calls).toEqual([{ command: COMMAND, args: ["arg"] }]);
    expect(target.requests).toEqual([{ command: COMMAND, arguments: ["arg"] }]);
  });
});
