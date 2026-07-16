import { beforeEach, describe, expect, it, vi } from "vitest";

const notifications = vi.hoisted(() => [] as Array<{ method: string; show: boolean | undefined }>);

vi.mock("monaco-languageclient", () => ({
  MonacoLanguageClient: class {
    handleFailedRequest<T>(
      type: { method: string },
      _token: unknown,
      _error: unknown,
      defaultValue: T,
      showNotification?: boolean,
    ): T {
      notifications.push({ method: type.method, show: showNotification });
      return defaultValue;
    }
  },
}));
vi.mock("vscode-languageclient", () => ({
  CodeLensRequest: { method: "textDocument/codeLens" },
  CodeLensResolveRequest: { method: "codeLens/resolve" },
  DocumentHighlightRequest: { method: "textDocument/documentHighlight" },
}));

import { createWeavieLanguageClient } from "./weavie-language-client";

beforeEach(() => notifications.splice(0));

describe("Weavie language client notifications", () => {
  it("suppresses passive provider failures but preserves deliberate navigation failures", () => {
    const client = createWeavieLanguageClient({} as never);
    const fail = (method: string, show: boolean | undefined): void => {
      client.handleFailedRequest({ method } as never, undefined, new Error("failed"), null, show);
    };

    fail("textDocument/codeLens", undefined);
    fail("codeLens/resolve", undefined);
    fail("textDocument/documentHighlight", undefined);
    fail("textDocument/references", undefined);
    fail("textDocument/rename", false);

    expect(notifications).toEqual([
      { method: "textDocument/codeLens", show: false },
      { method: "codeLens/resolve", show: false },
      { method: "textDocument/documentHighlight", show: false },
      { method: "textDocument/references", show: true },
      { method: "textDocument/rename", show: false },
    ]);
  });
});
