import { Disposable, workspace } from "vscode";
import type {
  SemanticTokensProviderShape,
  SemanticTokensRegistrationOptions,
} from "vscode-languageclient";
import { SemanticTokensFeature } from "vscode-languageclient/lib/common/semanticTokens.js";
import { SemanticTokenCache } from "./semantic-token-cache";

/** Keeps semantic requests scoped to provider/document lifetime rather than editor lifetime. */
export class SharedSemanticTokensFeature extends SemanticTokensFeature {
  protected override registerLanguageProvider(
    options: SemanticTokensRegistrationOptions,
  ): [Disposable, SemanticTokensProviderShape] {
    const [registration, providers] = super.registerLanguageProvider(options);
    const cache = new SemanticTokenCache({
      full: providers.full === undefined ? undefined : { ...providers.full },
      range: providers.range === undefined ? undefined : { ...providers.range },
    });
    if (providers.full !== undefined) {
      providers.full.provideDocumentSemanticTokens = (document, token) =>
        cache.read(document, token);
      if (providers.full.provideDocumentSemanticTokensEdits !== undefined) {
        providers.full.provideDocumentSemanticTokensEdits = (document, _previous, token) =>
          cache.read(document, token);
      }
    }
    if (providers.range !== undefined) {
      providers.range.provideDocumentRangeSemanticTokens = (document, range, token) =>
        cache.readRange(document, range, token);
    }
    return [
      Disposable.from(
        registration,
        providers.onDidChangeSemanticTokensEmitter.event(() => cache.refresh()),
        workspace.onDidChangeTextDocument(({ document, contentChanges }) => {
          if (contentChanges.length > 0) cache.invalidate(document);
        }),
        workspace.onDidCloseTextDocument((document) => cache.close(document)),
        cache,
      ),
      providers,
    ];
  }
}
