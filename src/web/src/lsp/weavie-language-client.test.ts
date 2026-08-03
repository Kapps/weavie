import { beforeEach, describe, expect, it, vi } from "vitest";

const runtime = vi.hoisted(() => ({
  notifications: [] as Array<{ method: string; show: boolean | undefined }>,
  stops: 0,
}));

vi.mock("monaco-languageclient", () => ({
  MonacoLanguageClient: class {
    state = 3;

    stop(): Promise<void> {
      runtime.stops += 1;
      return Promise.resolve();
    }

    handleFailedRequest<T>(
      type: { method: string },
      _token: unknown,
      _error: unknown,
      defaultValue: T,
      showNotification?: boolean,
    ): T {
      runtime.notifications.push({ method: type.method, show: showNotification });
      return defaultValue;
    }
  },
}));
vi.mock("vscode-languageclient", () => ({
  CodeLensRequest: { method: "textDocument/codeLens" },
  CodeLensResolveRequest: { method: "codeLens/resolve" },
  DocumentDiagnosticRequest: { method: "textDocument/diagnostic" },
  DocumentHighlightRequest: { method: "textDocument/documentHighlight" },
  State: { Stopped: 1, Running: 2, Starting: 3 },
}));

import { createWeavieLanguageClient } from "./weavie-language-client";

beforeEach(() => {
  runtime.notifications.splice(0);
  runtime.stops = 0;
});

describe("Weavie language client notifications", () => {
  it("suppresses passive provider failures but preserves deliberate navigation failures", () => {
    const client = createWeavieLanguageClient({} as never);
    const fail = (method: string, show: boolean | undefined): void => {
      client.handleFailedRequest({ method } as never, undefined, new Error("failed"), null, show);
    };

    fail("textDocument/codeLens", undefined);
    fail("codeLens/resolve", undefined);
    fail("textDocument/diagnostic", undefined);
    fail("textDocument/documentHighlight", undefined);
    fail("textDocument/references", undefined);
    fail("textDocument/rename", false);

    expect(runtime.notifications).toEqual([
      { method: "textDocument/codeLens", show: false },
      { method: "codeLens/resolve", show: false },
      { method: "textDocument/diagnostic", show: false },
      { method: "textDocument/documentHighlight", show: false },
      { method: "textDocument/references", show: true },
      { method: "textDocument/rename", show: false },
    ]);
  });

  it("does not ask the upstream client to stop before initialization finishes", async () => {
    const client = createWeavieLanguageClient({} as never);

    await client.stop();
    expect(runtime.stops).toBe(0);

    (client as unknown as { state: number }).state = 2;
    await client.stop();
    expect(runtime.stops).toBe(1);
  });
});
