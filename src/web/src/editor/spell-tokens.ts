import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { CancellationTokenSource } from "@codingame/monaco-vscode-api/vscode/vs/base/common/cancellation";
import { Range } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/core/range";
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

const modelTokens = new WeakMap<monaco.editor.ITextModel, ModelTokens>();

interface ModelTokens {
  listeners: Set<() => void>;
  read(ranges: SpellSpan[]): Promise<IdentifierRange[]>;
}

/** Declaration requests belong to the working model, so remounting an editor reuses them. */
function tokensForModel(current: monaco.editor.ITextModel): ModelTokens {
  const existing = modelTokens.get(current);
  if (existing !== undefined) return existing;
  const features = StandaloneServices.get(ILanguageFeaturesService);
  const listeners = new Set<() => void>();
  let providers: Provider[] = [];
  let providerSubscriptions: monaco.IDisposable[] = [];
  const pending = new Set<CancellationTokenSource>();
  let cached: { key: string; result: Promise<IdentifierRange[]> } | undefined;
  const invalidate = (): void => {
    for (const source of pending) source.dispose(true);
    pending.clear();
    cached = undefined;
  };
  const refresh = (): void => {
    invalidate();
    for (const listener of listeners) listener();
  };
  const bindProviders = (): void => {
    for (const subscription of providerSubscriptions) subscription.dispose();
    providers =
      features.documentRangeSemanticTokensProvider.orderedGroups(current)[0] ??
      features.documentSemanticTokensProvider.orderedGroups(current)[0] ??
      [];
    providerSubscriptions = providers.flatMap((provider) =>
      provider.onDidChange === undefined ? [] : [provider.onDidChange(refresh)],
    );
    invalidate();
  };
  const providerChanged = (): void => {
    bindProviders();
    for (const listener of listeners) listener();
  };
  const subscriptions = [
    features.documentSemanticTokensProvider.onDidChange(providerChanged),
    features.documentRangeSemanticTokensProvider.onDidChange(providerChanged),
    current.onDidChangeContent(invalidate),
    current.onDidChangeLanguage(providerChanged),
    current.onWillDispose(() => {
      invalidate();
      for (const subscription of [...subscriptions, ...providerSubscriptions])
        subscription.dispose();
      listeners.clear();
      modelTokens.delete(current);
    }),
  ];
  bindProviders();
  const fetch = (segments: Range[]): Promise<IdentifierRange[]> => {
    const source = new CancellationTokenSource();
    pending.add(source);
    return Promise.all(
      providers.flatMap((provider) =>
        ("provideDocumentSemanticTokens" in provider ? [null] : segments).map(async (range) => {
          const full = "provideDocumentSemanticTokens" in provider;
          const tokens = await (full
            ? provider.provideDocumentSemanticTokens(current, null, source.token)
            : provider.provideDocumentRangeSemanticTokens(current, range!, source.token));
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
      ),
    )
      .then((results) => results.flat())
      .finally(() => pending.delete(source));
  };
  const result: ModelTokens = {
    listeners,
    read: (ranges) => {
      // Folding splits the viewport, so each contiguous run of lines is its own request.
      const segments: Range[] = [];
      for (const line of [...new Set(ranges.map((range) => range.line))].sort((a, b) => a - b)) {
        const last = segments.at(-1);
        const end = current.getLineMaxColumn(line);
        if (last?.endLineNumber === line - 1)
          segments[segments.length - 1] = last.setEndPosition(line, end);
        else segments.push(new Range(line, 1, line, end));
      }
      const key = providers.every((provider) => "provideDocumentSemanticTokens" in provider)
        ? "document"
        : segments.map((range) => `${range.startLineNumber}:${range.endLineNumber}`).join(",");
      if (cached?.key !== key) {
        const request = { key, result: fetch(segments) };
        cached = request;
        void request.result.catch(() => {
          if (cached === request) cached = undefined;
        });
      }
      return cached.result;
    },
  };
  modelTokens.set(current, result);
  return result;
}

/** Each editor subscribes to its model's metadata and filters declarations to its viewport. */
export function createSpellingTokens(changed: () => void) {
  let currentTokens: ModelTokens | undefined;
  const listener = (): void => changed();
  return {
    async read(
      current: monaco.editor.ITextModel,
      ranges: SpellSpan[],
      signal: AbortSignal,
    ): Promise<IdentifierRange[]> {
      signal.throwIfAborted();
      const next = tokensForModel(current);
      if (currentTokens !== next) {
        currentTokens?.listeners.delete(listener);
        currentTokens = next;
        currentTokens.listeners.add(listener);
      }
      if (ranges.length === 0) return [];
      const tokens = await currentTokens.read(ranges);
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
      currentTokens?.listeners.delete(listener);
      currentTokens = undefined;
    },
  };
}
