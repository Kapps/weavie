// Monaco glue for the test matcher: queries the registered document-symbol providers for a model and slices
// header text from it, then delegates to the pure matcher in test-match.ts (which unit-tests without monaco).

import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { ILanguageFeaturesService } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/services/languageFeatures.service";
import type * as monaco from "monaco-editor";
import { CancellationToken } from "vscode-jsonrpc";
import { collectTests, type SymbolNode, type TestHit } from "./test-match";
import type { TestRule } from "./test-profile";

export type { SymbolNode, SymbolRange, TestHit } from "./test-match";
export { collectTests, innermostHitAt } from "./test-match";

/** Queries the registered document-symbol providers for `model` and returns the raw hierarchical symbols. */
export async function getDocumentSymbols(model: monaco.editor.ITextModel): Promise<SymbolNode[]> {
  const service = StandaloneServices.get(ILanguageFeaturesService);
  for (const provider of service.documentSymbolProvider.ordered(model)) {
    const symbols = await provider.provideDocumentSymbols(model, CancellationToken.None);
    if (symbols != null && symbols.length > 0) {
      return symbols as unknown as SymbolNode[];
    }
  }
  return [];
}

/** Discovers the test hits in `model` under `rule`, slicing header text from the model for `header` matching. */
export async function documentTestHits(
  model: monaco.editor.ITextModel,
  rule: TestRule,
): Promise<TestHit[]> {
  const symbols = await getDocumentSymbols(model);
  return collectTests(symbols, rule, (node) =>
    model.getValueInRange({
      startLineNumber: node.range.startLineNumber,
      startColumn: node.range.startColumn,
      endLineNumber: node.selectionRange.startLineNumber,
      endColumn: node.selectionRange.startColumn,
    }),
  );
}
