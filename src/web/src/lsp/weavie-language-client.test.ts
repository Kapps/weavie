import { beforeEach, describe, expect, it, vi } from "vitest";

const runtime = vi.hoisted(() => ({
  // Did the base client swallow the failure instead of rethrowing it?
  logged: [] as Array<{ message: string; show: boolean | "force" | undefined }>,
  swallow: false,
  stops: 0,
  features: [] as unknown[],
  options: [] as unknown[],
}));

vi.mock("monaco-editor", () => ({ editor: { registerCommand: () => ({ dispose: () => {} }) } }));

vi.mock("monaco-languageclient", () => ({
  MonacoLanguageClient: class {
    state = 3;
    middleware = {};

    constructor(options: unknown) {
      runtime.options.push(options);
      this.registerFeature({ registrationType: { method: "workspace/executeCommand" } });
    }

    registerFeature(feature: unknown): void {
      runtime.features.push(feature);
    }

    error(message: string, _data?: unknown, showNotification?: boolean | "force"): void {
      runtime.logged.push({ message, show: showNotification });
    }

    stop(): Promise<void> {
      runtime.stops += 1;
      return Promise.resolve();
    }

    handleFailedRequest<T>(
      _type: { method: string },
      _token: unknown,
      error: unknown,
      defaultValue: T,
    ): T {
      if (runtime.swallow) {
        return defaultValue; // upstream's dead-connection / content-modified paths return instead of throwing
      }
      throw error;
    }
  },
}));
vi.mock("vscode-languageclient");

import { setNotifySink } from "../notify/notify";
import { SessionExecuteCommandFeature } from "./session-execute-command-feature";
import { createWeavieLanguageClient } from "./weavie-language-client";

const raised: Array<{ level: string; message: string; key: string | undefined }> = [];
const commandNamespace = "test-channel";

beforeEach(() => {
  runtime.logged.splice(0);
  runtime.swallow = false;
  runtime.stops = 0;
  runtime.features.length = 0;
  runtime.options.length = 0;
  raised.length = 0;
  setNotifySink(
    (level, message, key) => raised.push({ level, message, key }),
    () => {},
  );
});

// The failure the base client rethrows, as the provider that invoked the request sees it.
function fail(method: string, error: unknown, show?: boolean): unknown {
  const client = createWeavieLanguageClient({} as never, commandNamespace);
  try {
    return client.handleFailedRequest({ method } as never, undefined, error, null, show);
  } catch (thrown) {
    return thrown;
  }
}

describe("Weavie language client notifications", () => {
  it("warns only for the requests the user invoked and waits on", () => {
    fail("textDocument/definition", new Error("no SDK"));
    fail("textDocument/references", new Error("no SDK"));
    fail("workspace/symbol", new Error("no SDK"));
    fail("workspace/executeCommand", new Error("no SDK"));
    fail("textDocument/documentHighlight", new Error("file too large"));
    fail("textDocument/semanticTokens/full", new Error("file too large"));
    fail("textDocument/documentSymbol", new Error("busy"));
    fail("textDocument/codeLens", new Error("busy"));

    expect(raised).toEqual([
      {
        level: "warn",
        message: "Go to Definition failed: no SDK",
        key: "lsp:textDocument/definition",
      },
      {
        level: "warn",
        message: "Find All References failed: no SDK",
        key: "lsp:textDocument/references",
      },
      {
        level: "warn",
        message: "Go to Symbol in Workspace failed: no SDK",
        key: "lsp:workspace/symbol",
      },
      { level: "warn", message: "Command failed: no SDK", key: "lsp:workspace/executeCommand" },
    ]);
  });

  it("stays silent when the request was cancelled or the failure was swallowed", () => {
    const cancelled = new Error("Canceled");
    cancelled.name = "Canceled";
    expect(fail("textDocument/definition", cancelled)).toBe(cancelled);

    runtime.swallow = true;
    expect(fail("textDocument/definition", new Error("connection inactive"))).toBeNull();

    expect(raised).toEqual([]);
  });

  it("leaves the notification to a caller that surfaces the failure itself", () => {
    fail("textDocument/definition", new Error("no SDK"), false);
    expect(raised).toEqual([]);
  });

  it("logs the base client's own failure reports instead of toasting them", () => {
    const client = createWeavieLanguageClient({} as never, commandNamespace);

    client.error("Server initialization failed.", new Error("no SDK"), "force");

    expect(runtime.logged).toEqual([{ message: "Server initialization failed.", show: false }]);
    expect(raised).toEqual([]);
  });

  it("does not ask the upstream client to stop before initialization finishes", async () => {
    const client = createWeavieLanguageClient({} as never, commandNamespace);

    await client.stop();
    expect(runtime.stops).toBe(0);

    (client as unknown as { state: number }).state = 2;
    await client.stop();
    expect(runtime.stops).toBe(1);
  });

  it("replaces the upstream process-global execute-command feature", () => {
    createWeavieLanguageClient({} as never, commandNamespace);

    expect(runtime.features).toHaveLength(1);
    expect(runtime.features[0]).toBeInstanceOf(SessionExecuteCommandFeature);
    expect(
      (runtime.features[0] as { registrationType: { method: string } }).registrationType.method,
    ).toBe("workspace/executeCommand");
  });

  it("injects exact-origin conversion in both directions", () => {
    createWeavieLanguageClient({ clientOptions: {} } as never, commandNamespace);
    const feature = runtime.features[0] as SessionExecuteCommandFeature;
    const options = runtime.options[0] as {
      clientOptions: {
        commandIdConverters: {
          protocol2Code(command: string): string;
          code2Protocol(command: string): string;
        };
      };
    };
    const converters = options.clientOptions.commandIdConverters;

    expect(converters.protocol2Code("textDocument/references")).toBe("textDocument/references");
    feature.initialize(
      { executeCommandProvider: { commands: ["gopls.add_dependency"] } },
      undefined,
    );
    const alias = converters.protocol2Code("gopls.add_dependency");

    expect(alias).not.toBe("gopls.add_dependency");
    expect(converters.code2Protocol(alias)).toBe("gopls.add_dependency");
  });
});
