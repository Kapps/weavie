// The warm pool of language clients. One live MonacoLanguageClient belongs to each session/server pair. Each
// client's providers match only models carrying its owner's URI namespace, so same-language clients can run in
// parallel without answering for one another's documents.

import * as monaco from "monaco-editor";
import type { MonacoLanguageClient } from "monaco-languageclient";
import { CloseAction, ErrorAction, State } from "vscode-languageclient";
import { type ClientSession, log } from "../bridge";
import { worktreeMatchBase } from "../editor/fs-path";
import {
  hostUriString,
  protocolUri,
  SESSION_FILE_SCHEME,
  sessionFileUri,
} from "../editor/session-uri";
import { PAGE_EPOCH } from "../messaging/page-epoch";
import { notify } from "../notify/notify";
import { LspStartError, openLspChannel } from "./lsp-bridge-transport";
import type { WeavieLspConfig, WeavieLspServer } from "./types";
import { createWeavieLanguageClient } from "./weavie-language-client";

// If a server crashes (or the WS drops) while documents are open, reconnect with capped exponential backoff
// so a broken server doesn't storm; a connection that stayed up past HEALTHY_UPTIME_MS resets the backoff.
const MAX_RECONNECT_ATTEMPTS = 5;
const HEALTHY_UPTIME_MS = 10_000;

/** A live client in the warm pool: the (backend, slot) it serves, its teardown, and a liveness probe. */
interface PooledClient {
  owner: ClientSession;
  teardown: () => void;
  alive: () => boolean;
}

// Keyed by (backendId, slot, serverId): one live client per language per worktree. A newline can't occur in a
// backend id, session slot, or server id, so the composite key never collides.
const pool = new Map<string, PooledClient>();
let channelSeq = 0;
// Backends told (once per page instance, before their first lsp/start) to drop channels from earlier epochs —
// a fresh page owns none, so without the reset every reload leaks one live server per language.
const epochReset = new WeakSet<ClientSession>();
// Servers whose document symbols already failed once this page — the toast fires once, not per refresh.
const symbolFailureWarned = new Set<string>();

function keyFor(owner: ClientSession, serverId: string): string {
  return `${owner.connection.id}\n${owner.address.slot}\n${owner.address.incarnation}\n${serverId}`;
}

/** What the manager supplies to start a client; the callbacks keep the pool ignorant of monaco model bookkeeping. */
export interface EnsureClientParams {
  config: WeavieLspConfig;
  server: WeavieLspServer;
  owner: ClientSession;
  /** Fired when a client finishes starting, so the test-lens provider refreshes. */
  onStarted: () => void;
  /** Re-read live at reconnect: is any open model under this worktree still served by this server? */
  hasOpenDoc: () => boolean;
}

/** Ensure a warm client for (backend, slot, server) exists, reusing the live one if present (idempotent). */
export function ensureClient(params: EnsureClientParams): void {
  const key = keyFor(params.owner, params.server.id);
  if (pool.get(key)?.alive()) {
    return;
  }
  connect(key, params, 0);
}

/** Tears down every language client owned by a session that closed. */
export function pruneSession(owner: ClientSession): void {
  for (const [key, client] of pool) {
    if (client.owner === owner) {
      client.teardown();
      pool.delete(key);
    }
  }
}

function describeError(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

// A glob that scopes a client's providers to its own worktree. Uses the SAME base normalization as the
// model→worktree mapping (worktreeMatchBase), so a file this client owns always matches its glob.
function worktreePattern(owner: ClientSession, workspace: string): string {
  return `${worktreeMatchBase(sessionFileUri(owner, workspace).fsPath)}/**`;
}

function connect(key: string, params: EnsureClientParams, attempt: number): void {
  const { config, server, owner, onStarted, hasOpenDoc } = params;
  if (!epochReset.has(owner)) {
    epochReset.add(owner);
    owner.feature("lsp").publish("reset", { epoch: PAGE_EPOCH });
  }
  const channelId = `lsp${++channelSeq}-${PAGE_EPOCH}`;
  let openedAt = 0;
  // Set on intentional teardown (switch/prune): the supervised reconnect stands down and a late exit is ignored.
  let torn = false;
  // Set once this attempt's outcome is decided, so a failed start and a server exit schedule at most one reconnect.
  let handled = false;
  let client: MonacoLanguageClient | undefined;
  let startPromise: Promise<void> | undefined;
  // `entry`, `channel`, and `startPromise` are assigned further down; the closures here forward-reference them,
  // which is safe because nothing calls a closure until this synchronous body has finished.

  // Still the pool's current client for this key? A switch/prune/newer attempt replaces `entry`, superseding us.
  const current = (): boolean => pool.get(key) === entry;

  const disposeClient = (): void => {
    const c = client;
    client = undefined;
    if (c === undefined) {
      return;
    }
    // dispose() rejects while the client is still 'starting'; wait for start to settle, then dispose, and swallow
    // either rejection — we're tearing down regardless.
    void Promise.allSettled(startPromise === undefined ? [] : [startPromise])
      .then(() => c.dispose())
      .catch(() => {});
  };

  // Drop this key from the pool, but only while we still own it — never evict a newer live client for the key.
  const forget = (): void => {
    if (current()) {
      pool.delete(key);
    }
  };

  // Post lsp/stop at most once: a server exit's fail() and a later teardown() (prune) both tear down, but the
  // channel must be stopped a single time.
  let channelDisposed = false;
  const disposeChannel = (): void => {
    if (channelDisposed) {
      return;
    }
    channelDisposed = true;
    channel?.dispose();
  };

  const superviseReconnect = (reason: string): void => {
    // Stand down if torn, or superseded — a newer client owns this key now.
    if (torn || !current()) {
      return;
    }
    if (!hasOpenDoc()) {
      forget(); // no document under this worktree needs it — let a future open restart it
      return;
    }
    const nextAttempt = openedAt > 0 && Date.now() - openedAt > HEALTHY_UPTIME_MS ? 1 : attempt + 1;
    if (nextAttempt > MAX_RECONNECT_ATTEMPTS) {
      forget();
      log(
        "error",
        `lsp: ${server.id} gave up after ${MAX_RECONNECT_ATTEMPTS} reconnects (${reason})`,
      );
      notify(
        "error",
        `${server.id} language intelligence is unavailable (${reason}). Check that its language server is installed and on PATH.`,
      );
      return;
    }
    // First failure of a streak: a self-dismissing warn so the user sees the hiccup immediately.
    if (nextAttempt === 1) {
      notify("warn", `${server.id} language intelligence interrupted (${reason}); reconnecting…`);
    }
    const delayMs = Math.min(1000 * 2 ** (nextAttempt - 1), 15_000);
    log(
      "warn",
      `lsp: ${server.id} ${reason}; reconnecting in ${delayMs}ms (attempt ${nextAttempt})`,
    );
    setTimeout(() => {
      // Re-check ownership: a switch/prune between scheduling and firing must not resurrect a superseded client.
      if (torn || !current()) {
        return;
      }
      if (hasOpenDoc()) {
        connect(key, params, nextAttempt);
      } else {
        forget();
      }
    }, delayMs);
  };

  // One funnel for every failure path — a failed initialize or a server exit/failure-to-start — so recovery (and
  // the warn/give-up toasts) runs exactly once per attempt.
  const fail = (reason: string, reconnect: boolean): void => {
    if (handled) {
      return;
    }
    handled = true;
    disposeClient();
    disposeChannel();
    if (reconnect) {
      superviseReconnect(reason);
    } else {
      forget();
      log("error", `lsp: ${server.id} unavailable (${reason})`);
      notify(
        "warn",
        `${server.id} language intelligence is unavailable (${reason}). Check that its language server is installed and on PATH.`,
      );
    }
  };

  const teardown = (): void => {
    torn = true;
    disposeClient();
    disposeChannel();
  };

  const entry: PooledClient = {
    owner,
    teardown,
    alive: () => !torn,
  };

  // Invariant: one live client per key. If a prior one is somehow still live here, a guard upstream let a duplicate
  // through — tear it down (it would otherwise be orphaned by the overwrite and double every provider it
  // registered, e.g. the "More Actions" menu) and log loudly so the real cause gets fixed, not masked.
  const prior = pool.get(key);
  if (prior?.alive()) {
    log(
      "error",
      `lsp: ${server.id} still had a live client at connect — orphan-prevention tore it down`,
    );
    prior.teardown();
  }
  pool.set(key, entry);

  // Open the bus channel on the backend that owns this slot. The correlated start result prevents constructing
  // a language client for a permanent host-side failure; later process exits take the reconnect path.
  const channel = openLspChannel(owner, server.id, channelId, (code, reason) => {
    if (torn || !current()) {
      return;
    }
    fail(reason ?? `server exited (code ${code})`, true);
  });
  openedAt = Date.now();

  const startClient = (): void => {
    const settings = server.settings ?? {};
    client = createWeavieLanguageClient({
      name: `Weavie ${server.id} language client`,
      clientOptions: {
        // Scope providers to this worktree so a warm client from another session never answers for this one's
        // files (and vice-versa) — the structural guard against duplicate code actions across worktrees.
        documentSelector: server.languageIds.map((language) => ({
          language,
          scheme: SESSION_FILE_SCHEME,
          pattern: worktreePattern(owner, config.workspace),
        })),
        workspaceFolder: {
          uri: sessionFileUri(owner, config.workspace),
          name: "weavie",
          index: 0,
        },
        uriConverters: {
          code2Protocol: (uri) => hostUriString(monaco.Uri.parse(uri.toString())),
          protocol2Code: (uri) => protocolUri(owner, uri),
        },
        // Feed the server its defaults both ways — initializationOptions and workspace/configuration answers
        // (some servers gate features on config, e.g. gopls semantic tokens). No VSCode config service (§18).
        initializationOptions: settings,
        middleware: {
          workspace: {
            configuration: (params) => params.items.map(() => settings),
          },
          // A malformed symbol from the server (e.g. an empty name, which the protocol converter rejects with
          // "name must not be falsy") must degrade to no outline for that file — not storm window.error on every
          // breadcrumb/outline refresh. Warned once per server; each occurrence still logs.
          provideDocumentSymbols: async (document, token, next) => {
            try {
              return await next(document, token);
            } catch (err) {
              // Routine request cancellation (rethrown by handleFailedRequest as the vscode shim's
              // CancellationError, whose stable marker is name === "Canceled") is not a malformed response.
              if (err instanceof Error && err.name === "Canceled") {
                throw err;
              }
              log("error", `lsp: ${server.id} document symbols failed: ${describeError(err)}`);
              if (!symbolFailureWarned.has(server.id)) {
                symbolFailureWarned.add(server.id);
                notify(
                  "warn",
                  `${server.id} returned document symbols Weavie couldn't read; the outline is unavailable for the affected file.`,
                );
              }
              return [];
            }
          },
        },
        // The client itself stays passive on errors; recovery is the host-supervised reconnect above.
        errorHandler: {
          error: () => ({ action: ErrorAction.Continue }),
          closed: () => ({ action: CloseAction.DoNotRestart }),
        },
      },
      messageTransports: { reader: channel.reader, writer: channel.writer },
    });

    // start() rejects when the server faults on initialize (e.g. csharp-ls with no resolvable SDK). Route that
    // through the same reconnect/give-up path as a server exit instead of leaking an unhandled rejection.
    startPromise = client.start();
    void startPromise.then(
      () => {
        if (client?.state !== State.Running) {
          fail("connection closed during initialization", true);
          return;
        }
        log("info", `lsp: ${server.id} client started`);
        onStarted();
      },
      (err: unknown) => fail(`initialize failed: ${describeError(err)}`, true),
    );
  };

  void channel.ready.then(
    () => {
      if (!torn && !handled && current()) {
        startClient();
      }
    },
    (error: unknown) => {
      if (!torn && current()) {
        fail(describeError(error), !(error instanceof LspStartError));
      }
    },
  );
}
