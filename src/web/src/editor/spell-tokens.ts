import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import { IExtensionResourceLoaderService } from "@codingame/monaco-vscode-api/vscode/vs/platform/extensionResourceLoader/common/extensionResourceLoader.service";
import { IWebWorkerService } from "@codingame/monaco-vscode-api/vscode/vs/platform/webWorker/browser/webWorkerService.service";
import { IExtensionService } from "@codingame/monaco-vscode-api/vscode/vs/workbench/services/extensions/common/extensions.service";
import type { monaco } from "./monaco-setup";
import type { SpellSpan } from "./spell-prose";
import type { GrammarDefinition, GrammarHost, IdentifierRange, SpellWorker } from "./spell-worker";
import WorkerConstructor from "./spell-worker-entry?worker";

type SpellSource = (
  version: number,
  ranges: SpellSpan[],
  signal: AbortSignal,
) => Promise<IdentifierRange[]>;
const models = new WeakMap<monaco.editor.ITextModel, Promise<SpellSource>>();

async function createSource(model: monaco.editor.ITextModel): Promise<SpellSource> {
  const worker = new WorkerConstructor();
  const client = StandaloneServices.get(IWebWorkerService).createWorkerClient<SpellWorker>(worker);
  const subscriptions: monaco.IDisposable[] = [];
  let disposed = false;
  let rejectFailure: (error: Error) => void = () => {};
  const failure = new Promise<never>((_resolve, reject) => {
    rejectFailure = reject;
  });
  const dispose = (error: Error): void => {
    if (disposed) return;
    disposed = true;
    models.delete(model);
    for (const subscription of subscriptions) subscription.dispose();
    client.dispose();
    rejectFailure(error);
  };
  worker.addEventListener("error", (event) => dispose(new Error(event.message)));
  subscriptions.push(
    model.onDidChangeLanguage(() => dispose(new DOMException("Language changed", "AbortError"))),
    model.onWillDispose(() => dispose(new DOMException("Model disposed", "AbortError"))),
  );
  const extensions = StandaloneServices.get(IExtensionService);
  const loader = StandaloneServices.get(IExtensionResourceLoaderService);
  client.setChannel<GrammarHost>("grammars", {
    $read: (location) => loader.readExtensionResource(URI.parse(location)),
  });
  const initialize = async (): Promise<void> => {
    await extensions.whenInstalledExtensionsRegistered();
    if (disposed) return;
    const definitions: GrammarDefinition[] = extensions.extensions.flatMap((extension) =>
      (extension.contributes?.grammars ?? []).map((grammar) => ({
        scope: grammar.scopeName,
        language: grammar.language,
        location: URI.joinPath(extension.extensionLocation, grammar.path).toString(),
        injectTo: grammar.injectTo ?? [],
      })),
    );
    const initialized = client.proxy.$init(
      definitions,
      model.getLanguageId(),
      model.uri.toString(),
      model.getLinesContent(),
      model.getEOL(),
      model.getVersionId(),
    );
    subscriptions.push(
      model.onDidChangeContent((event) => {
        void client.proxy.$update(event).catch((error: Error) => dispose(error));
      }),
    );
    await initialized;
  };
  try {
    await Promise.race([initialize(), failure]);
  } catch (error) {
    dispose(error instanceof Error ? error : new Error(String(error)));
    throw error;
  }
  let running: Promise<IdentifierRange[]> | undefined;
  return async (version, ranges, signal) => {
    while (running !== undefined) await running;
    signal.throwIfAborted();
    running = Promise.race([client.proxy.$tokens(version, ranges), failure]);
    try {
      return await running;
    } finally {
      running = undefined;
    }
  };
}

export async function spellingTokens(
  model: monaco.editor.ITextModel,
  ranges: SpellSpan[],
  signal: AbortSignal,
): Promise<IdentifierRange[]> {
  if (
    model.getLanguageId() === "plaintext" ||
    model.getLanguageId() === "markdown" ||
    ranges.length === 0
  )
    return [];
  let source = models.get(model);
  if (source === undefined) {
    source = createSource(model);
    models.set(model, source);
  }
  const version = model.getVersionId();
  return (await source)(version, ranges, signal);
}
