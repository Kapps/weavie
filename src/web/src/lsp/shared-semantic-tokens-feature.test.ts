import { CancellationToken } from "@codingame/monaco-vscode-api/vscode/vs/base/common/cancellation";
import { expect, it, vi } from "vitest";
import type { SemanticTokens, TextDocument } from "vscode";

const runtime = vi.hoisted(() => ({
  change: (_event: { document: TextDocument; contentChanges: unknown[] }) => {},
  close: (_document: TextDocument) => {},
  refresh: () => {},
  provide: vi.fn(),
  unregister: vi.fn(),
}));
vi.mock("vscode", () => ({
  Disposable: {
    from: (...items: { dispose(): void }[]) => ({
      dispose: () => {
        for (const item of items) item.dispose();
      },
    }),
  },
  workspace: {
    onDidChangeTextDocument: (listener: typeof runtime.change) => {
      runtime.change = listener;
      return { dispose() {} };
    },
    onDidCloseTextDocument: (listener: typeof runtime.close) => {
      runtime.close = listener;
      return { dispose() {} };
    },
  },
}));
vi.mock("vscode-languageclient/lib/common/semanticTokens.js", () => ({
  SemanticTokensFeature: class {
    registerLanguageProvider() {
      return [
        { dispose: runtime.unregister },
        {
          full: { provideDocumentSemanticTokens: runtime.provide },
          onDidChangeSemanticTokensEmitter: {
            event: (listener: () => void) => {
              runtime.refresh = listener;
              return { dispose() {} };
            },
          },
        },
      ];
    }
  },
}));

import { SharedSemanticTokensFeature } from "./shared-semantic-tokens-feature";

it("retains pending tokens on save but invalidates content, refresh, close, and registration lifetime", async () => {
  class Feature extends SharedSemanticTokensFeature {
    registerProvider() {
      return this.registerLanguageProvider({
        documentSelector: [],
        legend: { tokenTypes: [], tokenModifiers: [] },
      });
    }
  }
  const document = { version: 1, languageId: "typescript" } as TextDocument;
  const first = { data: new Uint32Array([0, 0, 1, 0, 0]), resultId: "first" };
  const pending = Promise.withResolvers<SemanticTokens>();
  runtime.provide.mockReturnValueOnce(pending.promise).mockResolvedValue(first);
  const [registration, provider] = new Feature({} as never).registerProvider();
  const read = () => provider.full!.provideDocumentSemanticTokens(document, CancellationToken.None);
  const initial = read();
  await Promise.resolve();
  runtime.change({ document, contentChanges: [] });
  pending.resolve(first);
  expect(await initial).toEqual(first);
  await read();
  expect(runtime.provide).toHaveBeenCalledTimes(1);
  runtime.change({ document, contentChanges: [{}] });
  await read();
  runtime.refresh();
  await read();
  runtime.close(document);
  await read();
  expect(runtime.provide).toHaveBeenCalledTimes(4);
  const late = Promise.withResolvers<SemanticTokens>();
  runtime.provide.mockReturnValueOnce(late.promise);
  runtime.refresh();
  const inFlight = read();
  await Promise.resolve();
  registration.dispose();
  late.resolve(first);
  expect(await inFlight).toBeNull();
  expect(runtime.unregister).toHaveBeenCalledOnce();
});
