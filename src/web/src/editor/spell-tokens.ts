import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { CancellationTokenSource } from "@codingame/monaco-vscode-api/vscode/vs/base/common/cancellation";
import type {
  DocumentRangeSemanticTokensProvider,
  DocumentSemanticTokensProvider,
  SemanticTokens,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/languages";
import { ILanguageFeaturesService } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/services/languageFeatures.service";
import type { monaco } from "./monaco-setup";
import type { SpellSpan } from "./spell-prose";

export interface IdentifierRange {
  line: number;
  startIndex: number;
  endIndex: number;
}

type Provider = DocumentSemanticTokensProvider | DocumentRangeSemanticTokensProvider;

function declarations(tokens: SemanticTokens, provider: Provider): IdentifierRange[] {
  const mask = provider
    .getLegend()
    .tokenModifiers.reduce(
      (mask, modifier, index) =>
        modifier === "declaration" || modifier === "definition" ? mask | (1 << index) : mask,
      0,
    );
  const ranges: IdentifierRange[] = [];
  let line = 1;
  let startIndex = 0;
  for (let index = 0; index < tokens.data.length; index += 5) {
    const deltaLine = tokens.data[index]!;
    line += deltaLine;
    startIndex = (deltaLine === 0 ? startIndex : 0) + tokens.data[index + 1]!;
    if ((tokens.data[index + 4]! & mask) !== 0) {
      ranges.push({ line, startIndex, endIndex: startIndex + tokens.data[index + 2]! });
    }
  }
  return ranges;
}

/** Owns declaration metadata for the editor's current model, shared across viewport checks. */
export function createSpellingTokens(changed: () => void) {
  const features = StandaloneServices.get(ILanguageFeaturesService);
  const registries = [
    features.documentSemanticTokensProvider,
    features.documentRangeSemanticTokensProvider,
  ];
  let model: monaco.editor.ITextModel | undefined;
  let providers: Provider[] = [];
  let modelSubscriptions: monaco.IDisposable[] = [];
  let providerSubscriptions: monaco.IDisposable[] = [];
  let pending: CancellationTokenSource | undefined;
  let cached: Promise<IdentifierRange[]> | undefined;
  const invalidate = (): void => {
    pending?.dispose(true);
    pending = undefined;
    cached = undefined;
  };
  const refresh = (): void => {
    invalidate();
    changed();
  };
  const bindProviders = (): void => {
    for (const subscription of providerSubscriptions) subscription.dispose();
    providers =
      model === undefined
        ? []
        : (features.documentSemanticTokensProvider.orderedGroups(model)[0] ??
          features.documentRangeSemanticTokensProvider.orderedGroups(model)[0] ??
          []);
    providerSubscriptions = providers.flatMap((provider) =>
      provider.onDidChange === undefined ? [] : [provider.onDidChange(refresh)],
    );
    invalidate();
  };
  const providerChanged = (): void => {
    bindProviders();
    changed();
  };
  const subscriptions = registries.map((registry) => registry.onDidChange(providerChanged));
  const unbind = (): void => {
    invalidate();
    for (const subscription of [...modelSubscriptions, ...providerSubscriptions])
      subscription.dispose();
    modelSubscriptions = [];
    providerSubscriptions = [];
    model = undefined;
    providers = [];
  };
  return {
    async read(
      current: monaco.editor.ITextModel,
      ranges: SpellSpan[],
      signal: AbortSignal,
    ): Promise<IdentifierRange[]> {
      signal.throwIfAborted();
      if (model !== current) {
        unbind();
        model = current;
        modelSubscriptions = [
          current.onDidChangeContent(invalidate),
          current.onDidChangeLanguage(providerChanged),
          current.onWillDispose(unbind),
        ];
        bindProviders();
      }
      if (ranges.length === 0) return [];
      if (cached === undefined) {
        const source = new CancellationTokenSource();
        pending = source;
        cached = Promise.all(
          providers.map(async (provider) => {
            const full = "provideDocumentSemanticTokens" in provider;
            const tokens = await (full
              ? provider.provideDocumentSemanticTokens(current, null, source.token)
              : provider.provideDocumentRangeSemanticTokens(
                  current,
                  current.getFullModelRange(),
                  source.token,
                ));
            try {
              if (source.token.isCancellationRequested)
                throw new DOMException("Model changed", "AbortError");
              if (tokens == null) return [];
              if (!("data" in tokens))
                throw new Error("Semantic-token provider returned edits without a previous result");
              return declarations(tokens, provider);
            } finally {
              if (full && tokens != null) provider.releaseDocumentSemanticTokens(tokens.resultId);
            }
          }),
        ).then((results) => results.flat());
        const request = cached;
        void request.catch(() => {
          if (cached === request) invalidate();
        });
      }
      const tokens = await cached;
      signal.throwIfAborted();
      const visible = new Map<number, SpellSpan[]>();
      for (const range of ranges) {
        const line = visible.get(range.line) ?? [];
        line.push(range);
        visible.set(range.line, line);
      }
      const unique = new Map<string, IdentifierRange>();
      for (const token of tokens) {
        for (const range of visible.get(token.line) ?? []) {
          const startIndex = Math.max(range.offset, token.startIndex);
          const endIndex = Math.min(range.offset + range.text.length, token.endIndex);
          if (startIndex < endIndex) {
            unique.set(`${token.line}:${startIndex}:${endIndex}`, {
              line: token.line,
              startIndex,
              endIndex,
            });
          }
        }
      }
      return [...unique.values()].sort(
        (left, right) => left.line - right.line || left.startIndex - right.startIndex,
      );
    },
    dispose(): void {
      unbind();
      for (const subscription of subscriptions) subscription.dispose();
    },
  };
}
