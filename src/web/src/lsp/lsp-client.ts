// The WebView half of LSP: monaco-languageclient connected to the host's language servers over the Weavie
// bridge (no socket of its own — see lsp-bridge-transport.ts), via injected `window.__WEAVIE_LSP__`. A client
// starts lazily — only when a document of its language first opens — so csharp-ls/gopls aren't spawned until a
// .cs/.go file appears.
//
// Each session has its OWN worktree root + slot. A session switch pushes the incoming config, but clients are
// KEPT WARM per worktree (see language-client-pool.ts) so switching back is instant, not a cold re-index. An
// open model is mapped to ITS OWN worktree's client by path, so a backgrounded worktree stays served correctly.

import * as monaco from "monaco-editor";
import { activeBackendId, onSessionMessage } from "../bridge";
import { isUnderRoot, uriHostPath, worktreeMatchBase } from "../editor/fs-path";
import { initEditorServices } from "../editor/vscode-services";
import { ensureClient, pruneForeignBackend, pruneUnloaded } from "./language-client-pool";

/** One language server the host offers: bridge selector id, the languages it serves, and its defaults. */
export interface WeavieLspServer {
  id: string;
  languageIds: string[];
  settings: Record<string, unknown> | null;
}

/** LSP discovery the C# host injects: the session's slot (frames are tagged with it), worktree root, + catalog. */
export interface WeavieLspConfig {
  slot: string;
  workspace: string;
  servers: WeavieLspServer[];
}

declare global {
  interface Window {
    /** LSP bridge config injected by the host before navigation; absent in a plain-browser dev run. */
    __WEAVIE_LSP__?: WeavieLspConfig;
  }
}

// The active session's config (its worktree root feeds currentWorkspaceRoot). Undefined until the host injects one.
let activeConfig: WeavieLspConfig | undefined;
// Every LOADED session's config, keyed by slot, so an open model maps to its own worktree's client — not the
// active session's. A session's config is recorded when it becomes active; dropped when it is unloaded.
const slotConfigs = new Map<string, { config: WeavieLspConfig; backendId: string }>();
let hooksInstalled = false;

// Listeners notified when a language client finishes starting (and on rebind), so consumers that query LSP
// providers — the test-lens provider — can refresh once a server is actually able to answer.
const languageClientStartedListeners = new Set<() => void>();

/** The active session's worktree root (from the LSP config), or undefined before the host injects one. */
export function currentWorkspaceRoot(): string | undefined {
  return activeConfig?.workspace;
}

/** Subscribes to language-client-started events (fired when a client starts or services rebind). Returns an unsubscribe. */
export function onLanguageClientStarted(listener: () => void): () => void {
  languageClientStartedListeners.add(listener);
  return () => {
    languageClientStartedListeners.delete(listener);
  };
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

// The loaded session whose worktree contains `path` (longest matching root wins, so a nested worktree beats its
// parent). Only the active backend's sessions are considered — a client can only reach its server over the
// active backend's transport, so a stranded foreign-backend config must never re-create a pruned client.
// Undefined for a path outside every reachable worktree.
function configForPath(path: string): { config: WeavieLspConfig; backendId: string } | undefined {
  const active = activeBackendId();
  let best: { config: WeavieLspConfig; backendId: string } | undefined;
  let bestLength = -1;
  for (const entry of slotConfigs.values()) {
    const root = worktreeMatchBase(entry.config.workspace);
    if (entry.backendId === active && isUnderRoot(path, root) && root.length > bestLength) {
      best = entry;
      bestLength = root.length;
    }
  }
  return best;
}

function hasOpenModelUnder(workspace: string, server: WeavieLspServer): boolean {
  return monaco.editor.getModels().some((model) => {
    if (model.uri.scheme !== "file" || !server.languageIds.includes(model.getLanguageId())) {
      return false;
    }
    return isUnderRoot(uriHostPath(model.uri), workspace);
  });
}

// Ensure the warm client that owns `model`'s worktree exists (idempotent). No-op for non-file models (review/diff
// overlays), a path outside every worktree, or a language no server serves.
function ensureForModel(model: monaco.editor.ITextModel): void {
  if (model.uri.scheme !== "file") {
    return;
  }
  const entry = configForPath(uriHostPath(model.uri));
  if (entry === undefined) {
    return;
  }
  const server = serverForLanguage(entry.config, model.getLanguageId());
  if (server === undefined) {
    return;
  }
  ensureClient({
    config: entry.config,
    server,
    backendId: entry.backendId,
    onStarted: notifyLanguageClientStarted,
    hasOpenDoc: () => hasOpenModelUnder(entry.config.workspace, server),
  });
}

function startForOpenModels(): void {
  for (const model of monaco.editor.getModels()) {
    ensureForModel(model);
  }
}

function recordConfig(config: WeavieLspConfig): void {
  activeConfig = config;
  slotConfigs.set(config.slot, { config, backendId: activeBackendId() });
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
  // A session unload/delete pushes session-list with that slot no longer loaded: drop its config and tear its
  // warm client down, else the client would keep serving — and a late frame would misroute to the active session.
  onSessionMessage((message, backendId) => {
    if (message.type !== "session-list") {
      return;
    }
    const loaded = new Set(message.sessions.filter((s) => s.loaded).map((s) => s.id));
    pruneUnloaded(backendId, loaded);
    for (const [slot, entry] of slotConfigs) {
      if (entry.backendId === backendId && !loaded.has(slot)) {
        slotConfigs.delete(slot);
      }
    }
  });
}

/**
 * Wires lazy, per-language LSP for the session loaded at launch. No-op without injected bridge config
 * (plain-browser dev) — the editor still works, just without language intelligence.
 */
export async function startLanguageServices(): Promise<void> {
  const config = window.__WEAVIE_LSP__;
  if (config === undefined) {
    return;
  }
  // The editor's VSCode services must be initialized (with our overrides) before any monaco.editor.* call below —
  // touching monaco auto-initializes the standalone services, which then makes the editor host's initialize()
  // throw "Services are already initialized". initEditorServices() is idempotent, so this just enforces order.
  await initEditorServices();
  recordConfig(config);
  installHooks();
  startForOpenModels();
}

/**
 * Rebind language services on a session switch: record the incoming session and ensure its clients, but KEEP
 * same-backend clients warm (only a backend switch strands them). Switching back reuses the warm client — no
 * cold re-index. See language-client-pool.ts for the pool + per-worktree provider scoping.
 */
export async function rebindLanguageServices(config: WeavieLspConfig): Promise<void> {
  // Same init-order guard as startLanguageServices (see there): our init must precede any monaco.editor touch.
  await initEditorServices();
  recordConfig(config);
  installHooks();
  pruneForeignBackend(activeBackendId());
  startForOpenModels();
  // Switch-back reuses a warm client and fires no per-client start event; nudge consumers (test-lens) once here.
  notifyLanguageClientStarted();
}
