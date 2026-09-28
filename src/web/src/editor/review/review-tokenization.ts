import {
  DisposableStore,
  toDisposable,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import { TokenizationRegistry } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/languages";
import type { IAttachedView } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model";
import type { TextModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import type { TokenizationTextModelPart } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/tokens/tokenizationTextModelPart";

function grammarOwner(language: string): object | null {
  // Factory replacement emits no event; identity prevents accumulating per-frame load promises.
  const factories = (TokenizationRegistry as unknown as { _factories: ReadonlyMap<string, object> })
    ._factories;
  if (!(factories instanceof Map)) throw new Error("Unsupported Monaco tokenizer registry");
  return TokenizationRegistry.get(language) ?? factories.get(language) ?? null;
}

/** Keeps Monaco's background tokenizer alive without a widget or synchronous visible ranges. */
export class ReviewTokenization {
  private readonly lifetime = new AbortController();
  private readonly view: IAttachedView;
  private readonly disposal: { dispose(): void };
  private grammar:
    | { language: string; owner: object | null; failure: { error: unknown } | undefined }
    | undefined;

  public constructor(private readonly model: TextModel) {
    this.view = model.onBeforeAttached();
    this.disposal = model.onWillDispose(() => this.dispose());
  }

  private loadGrammar(language: string): void {
    const owner = grammarOwner(language);
    if (this.grammar?.language !== language || this.grammar.owner !== owner) {
      const load = { language, owner, failure: undefined as { error: unknown } | undefined };
      this.grammar = load;
      void TokenizationRegistry.getOrCreate(language).catch((error: unknown) => {
        load.failure = { error };
      });
    }
    if (this.grammar.failure !== undefined) throw this.grammar.failure.error;
  }

  public whenAccurate(version: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve, reject) => {
      const subscriptions = new DisposableStore();
      let frame: number | undefined;
      let settled = false;
      const finish = (complete: () => void): void => {
        if (settled) return;
        settled = true;
        if (frame !== undefined) cancelAnimationFrame(frame);
        subscriptions.dispose();
        complete();
      };
      const fail = (error: unknown): void => finish(() => reject(error));
      const obsolete = (): void =>
        fail(new DOMException("Review tokenization owner changed", "AbortError"));
      if (signal.aborted || this.lifetime.signal.aborted || this.model.isDisposed()) {
        obsolete();
        return;
      }
      const language = this.model.getLanguageId();
      if (this.model.getVersionId() !== version) {
        obsolete();
        return;
      }
      subscriptions.add(this.model.onDidChangeContent(obsolete));
      subscriptions.add(this.model.onDidChangeLanguage(obsolete));
      for (const cancellation of [signal, this.lifetime.signal]) {
        cancellation.addEventListener("abort", obsolete);
        subscriptions.add(toDisposable(() => cancellation.removeEventListener("abort", obsolete)));
      }
      const check = (): void => {
        frame = undefined;
        if (settled) return;
        try {
          if (
            this.model.isDisposed() ||
            this.model.getVersionId() !== version ||
            this.model.getLanguageId() !== language
          ) {
            obsolete();
            return;
          }
          this.loadGrammar(language);
          if (!TokenizationRegistry.isResolved(language)) {
            frame = requestAnimationFrame(check);
            return;
          }
          const backend = (this.model.tokenization as TokenizationTextModelPart).tokens.get();
          for (let line = this.model.getLineCount(); line >= 1; line--) {
            if (!backend.hasAccurateTokensForLine(line)) {
              frame = requestAnimationFrame(check);
              return;
            }
          }
          finish(resolve);
        } catch (error) {
          fail(error);
        }
      };
      // Completion events can precede final tokens, and identical-token edits may emit none.
      frame = requestAnimationFrame(check);
    });
  }

  public dispose(): void {
    if (this.lifetime.signal.aborted) return;
    this.lifetime.abort();
    this.disposal.dispose();
    this.model.onBeforeDetached(this.view);
  }
}
