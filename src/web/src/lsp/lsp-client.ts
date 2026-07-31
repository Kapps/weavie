import * as monaco from "monaco-editor";
import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import { isUnderRoot } from "../editor/fs-path";
import { SESSION_FILE_SCHEME, sessionForUri, sessionUriHostPath } from "../editor/session-uri";
import { initEditorServices } from "../editor/vscode-services";
import { ensureClient, pruneSession } from "./language-client-pool";
import type { WeavieLspConfig, WeavieLspServer } from "./types";

export type { WeavieLspConfig, WeavieLspServer } from "./types";

let servicesStarted = false;
let hooksInstalled = false;
const languageClientStartedListeners = new Set<() => void>();

export function currentWorkspaceRoot(): string | undefined {
  return selectedSession()?.state.lsp.current?.workspace;
}

export function onLanguageClientStarted(listener: () => void): () => void {
  languageClientStartedListeners.add(listener);
  return () => languageClientStartedListeners.delete(listener);
}

function notifyLanguageClientStarted(): void {
  for (const listener of languageClientStartedListeners) {
    listener();
  }
}

function serverForLanguage(
  config: WeavieLspConfig,
  languageId: string,
): WeavieLspServer | undefined {
  return config.servers.find((server) => server.languageIds.includes(languageId));
}

function hasOpenModel(owner: ClientSession, workspace: string, server: WeavieLspServer): boolean {
  return monaco.editor
    .getModels()
    .some(
      (model) =>
        model.uri.scheme === SESSION_FILE_SCHEME &&
        sessionForUri(model.uri) === owner &&
        server.languageIds.includes(model.getLanguageId()) &&
        isUnderRoot(sessionUriHostPath(model.uri), workspace),
    );
}

function ensureForModel(model: monaco.editor.ITextModel): void {
  if (model.uri.scheme !== SESSION_FILE_SCHEME) {
    return;
  }
  const owner = sessionForUri(model.uri);
  if (owner === undefined) {
    return;
  }
  const config = owner.state.lsp.current;
  if (config === null || !isUnderRoot(sessionUriHostPath(model.uri), config.workspace)) {
    return;
  }
  const server = serverForLanguage(config, model.getLanguageId());
  if (server === undefined) {
    return;
  }
  ensureClient({
    owner,
    config,
    server,
    onStarted: notifyLanguageClientStarted,
    hasOpenDoc: () => hasOpenModel(owner, config.workspace, server),
  });
}

function startForOpenModels(): void {
  for (const model of monaco.editor.getModels()) {
    ensureForModel(model);
  }
}

function installHooks(): void {
  if (hooksInstalled) {
    return;
  }
  hooksInstalled = true;
  monaco.editor.onDidCreateModel((model) => {
    ensureForModel(model);
    model.onDidChangeLanguage(() => ensureForModel(model));
  });
}

registerSessionFeature((session) => {
  let appliedConfig: string | undefined;
  const unsubscribe = session.state.lsp.subscribe((config) => {
    if (config === null) {
      return;
    }
    const fingerprint = JSON.stringify(config);
    if (appliedConfig !== undefined && appliedConfig !== fingerprint) {
      pruneSession(session);
    }
    appliedConfig = fingerprint;
    if (servicesStarted) {
      startForOpenModels();
      notifyLanguageClientStarted();
    }
  });
  return () => {
    unsubscribe();
    pruneSession(session);
  };
});

export async function startLanguageServices(): Promise<void> {
  await initEditorServices();
  servicesStarted = true;
  installHooks();
  startForOpenModels();
}
