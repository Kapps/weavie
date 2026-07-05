// The warm pool of language clients. One live MonacoLanguageClient per (backend, session slot, server) — so a
// session switch KEEPS the outgoing worktree's server warm instead of cold-starting it on switch-back. Each
// client's providers are scoped to its own worktree (documentSelector `pattern`), so two same-language clients
// (worktree A + B) never both answer for one file — what keeps warm background clients from doubling the
// "More Actions" menu. See docs/specs/warm-lsp-across-switch.md.

import * as monaco from "monaco-editor";
import { MonacoLanguageClient } from "monaco-languageclient";
import { CloseAction, ErrorAction } from "vscode-languageclient";
import { log } from "../bridge";
import { canonicalFsPath } from "../editor/fs-path";
import { notify } from "../notify/notify";
import { openLspChannel } from "./lsp-bridge-transport";
import type { WeavieLspConfig, WeavieLspServer } from "./lsp-client";

// If a server crashes (or the WS drops) while documents are open, reconnect with capped exponential backoff
// so a broken server doesn't storm; a connection that stayed up past HEALTHY_UPTIME_MS resets the backoff.
const MAX_RECONNECT_ATTEMPTS = 5;
const HEALTHY_UPTIME_MS = 10_000;

/** A live client in the warm pool: the (backend, slot) it serves, its teardown, and a liveness probe. */
interface PooledClient {
  backendId: string;
  slot: string;
  teardown: () => void;
  alive: () => boolean;
}

// Keyed by (backendId, slot, serverId): one live client per language per worktree. A newline can't occur in a
// backend id, session slot, or server id, so the composite key never collides.
const pool = new Map<string, PooledClient>();
let channelSeq = 0;

function keyFor(backendId: string, slot: string, serverId: string): string {
  return `${backendId}\n${slot}\n${serverId}`;
}

/** What the manager supplies to start a client; the callbacks keep the pool ignorant of monaco model bookkeeping. */
export interface EnsureClientParams {
  config: WeavieLspConfig;
  server: WeavieLspServer;
  backendId: string;
  /** Fired when a client finishes starting, so the test-lens provider refreshes. */
  onStarted: () => void;
  /** Re-read live at reconnect: is any open model under this worktree still served by this server? */
  hasOpenDoc: () => boolean;
}

/** Ensure a warm client for (backend, slot, server) exists, reusing the live one if present (idempotent). */
export function ensureClient(params: EnsureClientParams): void {
  const key = keyFor(params.backendId, params.config.slot, params.server.id);
  if (pool.get(key)?.alive()) {
    return;
  }
  connect(key, params, 0);
}

/** Tear down every warm client not on `activeBackendId` — a backend switch strands their bridge transport. */
export function pruneForeignBackend(activeBackendId: string): void {
  for (const [key, client] of pool) {
    if (client.backendId !== activeBackendId) {
      client.teardown();
      pool.delete(key);
    }
  }
}

/** Tear down warm clients on `backendId` whose slot is no longer loaded — the session was unloaded/deleted. */
export function pruneUnloaded(backendId: string, loadedSlots: Set<string>): void {
  for (const [key, client] of pool) {
    if (client.backendId === backendId && !loadedSlots.has(client.slot)) {
      client.teardown();
      pool.delete(key);
    }
  }
}

function describeError(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

// A glob that scopes a client's providers to its own worktree. Canonicalized the same way model URIs are
// (`canonicalFsPath`, forward slashes, no trailing slash) so it matches this worktree's files and only those.
function worktreePattern(workspace: string): string {
  return `${canonicalFsPath(workspace).replace(/\\/g, "/").replace(/\/+$/, "")}/**`;
}

function connect(key: string, params: EnsureClientParams, attempt: number): void {
  const { config, server, backendId, onStarted, hasOpenDoc } = params;
  const channelId = `lsp${++channelSeq}`;
  let openedAt = 0;
  // Set on intentional teardown (switch/prune): the supervised reconnect stands down and a late exit is ignored.
  let torn = false;
  // Set once this attempt's outcome is decided, so a failed start and a server exit schedule at most one reconnect.
  let handled = false;
  let client: MonacoLanguageClient | undefined;
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
    void Promise.allSettled([startPromise])
      .then(() => c.dispose())
      .catch(() => {});
  };

  // Drop this key from the pool, but only while we still own it — never evict a newer live client for the key.
  const forget = (): void => {
    if (current()) {
      pool.delete(key);
    }
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
  const fail = (reason: string): void => {
    if (handled) {
      return;
    }
    handled = true;
    disposeClient();
    channel?.dispose();
    superviseReconnect(reason);
  };

  const teardown = (): void => {
    torn = true;
    disposeClient();
    channel?.dispose();
  };

  const entry: PooledClient = {
    backendId,
    slot: config.slot,
    teardown,
    alive: () => client !== undefined,
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

  // Open the bridge channel: the host spawns the server on lsp-start; its exit or failure-to-start arrives via
  // onExit (carrying the host-side reason), routed through the same supervised reconnect as a dropped link.
  const channel = openLspChannel(config.slot, server.id, channelId, (code, reason) => {
    if (torn || !current()) {
      return;
    }
    fail(reason ?? `server exited (code ${code})`);
  });
  openedAt = Date.now();

  const settings = server.settings ?? {};
  client = new MonacoLanguageClient({
    name: `Weavie ${server.id} language client`,
    clientOptions: {
      // Scope providers to this worktree so a warm client from another session never answers for this one's
      // files (and vice-versa) — the structural guard against duplicate code actions across worktrees.
      documentSelector: server.languageIds.map((language) => ({
        language,
        scheme: "file",
        pattern: worktreePattern(config.workspace),
      })),
      workspaceFolder: {
        uri: monaco.Uri.file(config.workspace),
        name: "weavie",
        index: 0,
      },
      // Feed the server its defaults both ways — initializationOptions and workspace/configuration answers
      // (some servers gate features on config, e.g. gopls semantic tokens). No VSCode config service (§18).
      initializationOptions: settings,
      middleware: {
        workspace: {
          configuration: (params) => params.items.map(() => settings),
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
  const startPromise = client.start();
  void startPromise.then(
    () => {
      log("info", `lsp: ${server.id} client started`);
      onStarted();
    },
    (err: unknown) => fail(`initialize failed: ${describeError(err)}`),
  );
}
