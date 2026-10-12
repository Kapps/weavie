import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import { TextMateWorkerTokenizer } from "@codingame/monaco-vscode-textmate-service-override/vscode/vs/workbench/services/textMate/browser/backgroundTokenization/worker/textMateWorkerTokenizer";
import type { ICreateGrammarResult } from "@codingame/monaco-vscode-textmate-service-override/vscode/vs/workbench/services/textMate/common/TMGrammarFactory";
import { expect, test } from "vitest";

// Review editors attach and detach models as sections scroll, so the worker disposes tokenizers mid-pass.
test("a tokenizer disposed while its first pass awaits the textmate import stops cleanly", async () => {
  const grammar = { grammar: {}, initialState: {} } as unknown as ICreateGrammarResult;
  const tokenizer = new TextMateWorkerTokenizer(
    URI.file("/a.ts"),
    ["const a = 1;", "const b = 2;"],
    "\n",
    1,
    {
      getOrCreateGrammar: async () => grammar,
      setTokensAndStates: () => {},
      reportTokenizationTime: () => {},
    },
    "typescript",
    1,
    20_000,
  );
  const internals = tokenizer as unknown as {
    _tokenizerWithStateStore: unknown;
    _tokenize(): Promise<void>;
  };
  while (!internals._tokenizerWithStateStore) await Promise.resolve();
  const pass = internals._tokenize();
  tokenizer.dispose();
  await expect(pass).resolves.toBeUndefined();
});
