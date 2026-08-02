// The reactive glue between the omnibar's @ / # modes and the editor's symbol surface: fetches document symbols
// once per activation, debounces + cancels the live workspace-symbol query per keystroke, and exposes the ranked
// rows plus an honest status so the omnibar never renders a silent empty.

import { type Accessor, createEffect, createMemo, createSignal, on, onCleanup } from "solid-js";
import { log } from "../bridge";
import {
  rankSymbols,
  type ScoredSymbol,
  type SymbolActions,
  type SymbolQueryResult,
} from "./symbol-match";

export type SymbolMode = "docSymbol" | "wsSymbol";
/** idle shows the prompt; loading marks an active fetch; the other values describe its result. */
export type SymbolStatus = "idle" | "loading" | "noProvider" | "empty" | "ready" | "error";

// A workspace query is a live LSP round-trip, so debounce keystrokes; document symbols are fetched once (locally).
const WS_DEBOUNCE_MS = 150;

export interface SymbolSearch {
  /** The ranked rows to render (query-filtered, with highlight positions). */
  view: Accessor<ScoredSymbol[]>;
  /** The honest state to render when there are no rows (or while loading). */
  status: Accessor<SymbolStatus>;
}

/** Settles an async symbol query without surfacing a rejection after its reactive owner became obsolete. */
export function settleSymbolQuery<T>(
  query: Promise<T>,
  obsolete: () => boolean,
  resolved: (value: T) => void,
  rejected: (error: unknown) => void,
): void {
  void query.then(
    (value) => {
      if (!obsolete()) {
        resolved(value);
      }
    },
    (error: unknown) => {
      if (!obsolete()) {
        rejected(error);
      }
    },
  );
}

/**
 * Wires the omnibar's symbol modes to `symbols`. `active` is the current symbol mode (null when the omnibar isn't
 * in one); `query` is the text after the @ / # prefix; `reloadKey` bumps to force a document-symbol refetch (the
 * omnibar opened, or the active file changed).
 */
export function createSymbolSearch(deps: {
  active: Accessor<SymbolMode | null>;
  query: Accessor<string>;
  reloadKey: Accessor<unknown>;
  symbols: SymbolActions;
}): SymbolSearch {
  // The last completed query result from the source; null means "loading / nothing fetched yet / cleared".
  const [result, setResult] = createSignal<SymbolQueryResult | null>(null);
  const [failed, setFailed] = createSignal(false);

  const reportFailure = (kind: string, error: unknown): void => {
    const detail = error instanceof Error ? (error.stack ?? error.message) : String(error);
    log("error", `${kind} symbol query failed: ${detail}`);
    setResult(null);
    setFailed(true);
  };

  // Drop stale rows the instant the mode changes, so the previous mode's (or file's) symbols never flash under a
  // fresh query. Query-only changes within a mode keep the prior rows visible while the next fetch runs.
  createEffect(
    on(deps.active, () => {
      setResult(null);
      setFailed(false);
    }),
  );

  // Document symbols: fetch the whole file's symbols once per activation / file change (not per keystroke — the
  // query only re-ranks the fetched set below).
  createEffect(
    on([deps.active, deps.reloadKey], ([mode]) => {
      if (mode !== "docSymbol") {
        return;
      }
      setFailed(false);
      let cancelled = false;
      onCleanup(() => {
        cancelled = true;
      });
      settleSymbolQuery(
        deps.symbols.documentSymbols(),
        () => cancelled,
        setResult,
        (error) => reportFailure("Document", error),
      );
    }),
  );

  // Workspace symbols: debounced, abortable query per keystroke. An empty query stays idle (no blank-string LSP hit).
  createEffect(
    on([deps.active, deps.query], ([mode, rawQuery]) => {
      if (mode !== "wsSymbol") {
        return;
      }
      const q = rawQuery.trim();
      if (q.length === 0) {
        setResult(null);
        setFailed(false);
        return;
      }
      setFailed(false);
      const controller = new AbortController();
      let cancelled = false;
      onCleanup(() => {
        cancelled = true;
        controller.abort();
      });
      const timer = setTimeout(() => {
        settleSymbolQuery(
          deps.symbols.workspaceSymbols(q, controller.signal),
          () => cancelled,
          setResult,
          (error) => reportFailure("Workspace", error),
        );
      }, WS_DEBOUNCE_MS);
      onCleanup(() => clearTimeout(timer));
    }),
  );

  const view = createMemo<ScoredSymbol[]>(() => {
    const r = result();
    return r === null ? [] : rankSymbols(r.items, deps.query());
  });

  const status = createMemo<SymbolStatus>(() => {
    const mode = deps.active();
    if (mode === null || (mode === "wsSymbol" && deps.query().trim().length === 0)) {
      return "idle";
    }
    if (failed()) {
      return "error";
    }
    const r = result();
    if (r === null) {
      return "loading";
    }
    if (!r.providerAvailable) {
      return "noProvider";
    }
    return view().length === 0 ? "empty" : "ready";
  });

  return { view, status };
}
