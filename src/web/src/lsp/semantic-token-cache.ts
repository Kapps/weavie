import { CancellationTokenSource } from "@codingame/monaco-vscode-api/vscode/vs/base/common/cancellation";
import type {
  CancellationToken,
  DocumentRangeSemanticTokensProvider,
  DocumentSemanticTokensProvider,
  Range,
  SemanticTokens,
  SemanticTokensEdits,
  TextDocument,
} from "vscode";

type Tokens = SemanticTokens | null | undefined;
interface Request {
  source: CancellationTokenSource;
  result: Promise<Tokens>;
}
interface DocumentTokens {
  version: number;
  language: string;
  previous: SemanticTokens | undefined;
  full: Request | undefined;
  ranges: Map<string, Request>;
}

function completeTokens(
  result: Tokens | SemanticTokensEdits,
  previous: SemanticTokens | undefined,
): Tokens {
  if (result == null || "data" in result) return result;
  if (previous === undefined) throw new Error("Semantic token edits have no base snapshot");
  const edits = [...result.edits].sort((left, right) => left.start - right.start);
  const data = new Uint32Array(
    edits.reduce(
      (length, edit) => length + (edit.data?.length ?? 0) - edit.deleteCount,
      previous.data.length,
    ),
  );
  let read = 0;
  let written = 0;
  for (const edit of edits) {
    if (edit.start < read || edit.start + edit.deleteCount > previous.data.length)
      throw new Error("Semantic token edit is outside its base snapshot");
    data.set(previous.data.subarray(read, edit.start), written);
    written += edit.start - read;
    if (edit.data !== undefined) data.set(edit.data, written);
    written += edit.data?.length ?? 0;
    read = edit.start + edit.deleteCount;
  }
  data.set(previous.data.subarray(read), written);
  return { data, resultId: result.resultId };
}

function waitFor(request: Request, token: CancellationToken): Promise<Tokens> {
  if (token.isCancellationRequested) return Promise.resolve(null);
  return new Promise((resolve, reject) => {
    const subscription = token.onCancellationRequested(() => {
      subscription.dispose();
      resolve(null);
    });
    void request.result.then(
      (result) => {
        subscription.dispose();
        resolve(token.isCancellationRequested ? null : result);
      },
      (error: unknown) => {
        subscription.dispose();
        reject(error);
      },
    );
  });
}

/** One provider/document snapshot serves highlighting, spelling, and editor reattachment. */
export class SemanticTokenCache {
  private readonly documents = new Map<TextDocument, DocumentTokens>();

  constructor(
    private readonly providers: {
      full: DocumentSemanticTokensProvider | undefined;
      range: DocumentRangeSemanticTokensProvider | undefined;
    },
  ) {}

  read(document: TextDocument, token: CancellationToken): Promise<Tokens> {
    if (token.isCancellationRequested) return Promise.resolve(null);
    const state = this.state(document);
    state.full ??= this.request(
      async (source) => {
        const previous = state.previous;
        const result =
          previous?.resultId !== undefined &&
          this.providers.full!.provideDocumentSemanticTokensEdits
            ? await this.providers.full!.provideDocumentSemanticTokensEdits(
                document,
                previous.resultId,
                source,
              )
            : await this.providers.full!.provideDocumentSemanticTokens(document, source);
        const tokens = completeTokens(result, previous);
        if (!source.isCancellationRequested && tokens != null) state.previous = tokens;
        return tokens;
      },
      () => {
        state.full = undefined;
        state.previous = undefined;
      },
    );
    return waitFor(state.full, token);
  }

  readRange(document: TextDocument, range: Range, token: CancellationToken): Promise<Tokens> {
    if (token.isCancellationRequested) return Promise.resolve(null);
    const state = this.state(document);
    const key = `${range.start.line}:${range.start.character}:${range.end.line}:${range.end.character}`;
    let request = state.ranges.get(key);
    if (request === undefined) {
      request = this.request(
        (source) =>
          Promise.resolve(
            this.providers.range!.provideDocumentRangeSemanticTokens(document, range, source),
          ),
        () => {},
      );
      const current = request;
      current.result = current.result.finally(() => {
        if (state.ranges.get(key) === current) state.ranges.delete(key);
      });
      state.ranges.set(key, current);
    }
    return waitFor(request, token);
  }

  invalidate(document: TextDocument): void {
    const state = this.documents.get(document);
    if (state === undefined) return;
    state.full?.source.dispose(true);
    for (const request of state.ranges.values()) request.source.dispose(true);
    state.full = undefined;
    state.ranges.clear();
  }

  close(document: TextDocument): void {
    this.invalidate(document);
    this.documents.delete(document);
  }

  refresh(): void {
    for (const document of this.documents.keys()) this.invalidate(document);
  }

  dispose(): void {
    this.refresh();
    this.documents.clear();
  }

  private state(document: TextDocument): DocumentTokens {
    let state = this.documents.get(document);
    if (state === undefined || state.language !== document.languageId) {
      this.close(document);
      state = {
        version: document.version,
        language: document.languageId,
        previous: undefined,
        full: undefined,
        ranges: new Map(),
      };
      this.documents.set(document, state);
    } else if (state.version !== document.version) {
      this.invalidate(document);
      state.version = document.version;
    }
    return state;
  }

  private request(
    fetch: (token: CancellationToken) => Promise<Tokens>,
    failed: () => void,
  ): Request {
    const source = new CancellationTokenSource();
    const token = source.token;
    const result = Promise.resolve()
      .then(() => (token.isCancellationRequested ? null : fetch(token)))
      .then(
        (tokens) => {
          if (token.isCancellationRequested) return null;
          if (tokens == null) failed();
          return tokens;
        },
        (error: unknown) => {
          if (!token.isCancellationRequested) failed();
          throw error;
        },
      )
      .finally(() => source.dispose());
    return { source, result };
  }
}
