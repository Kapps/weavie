// The pure symbol logic — no monaco imports, so it unit-tests in isolation (mirrors test-match.ts). Flattens a
// document-symbol tree into jump-ready rows and fuzzy-ranks them for the omnibar's @ / # modes. The monaco glue
// that sources the trees lives in symbol-source.ts; the reactive orchestration in symbol-search.ts.

import { byLengthAsc, Fzf } from "fzf";

/** A 1-based range (matches monaco's IRange). */
export interface SymbolRange {
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
}

/** The subset of a monaco DocumentSymbol the flattener needs; `kind` is already a semantic label (see kindLabel). */
export interface DocSymbolNode {
  name: string;
  kind: string;
  selectionRange: SymbolRange;
  children?: DocSymbolNode[];
}

/** A flat, jump-ready symbol: display name, its kind + container chain, the file it lives in, and the 1-based nav range. */
export interface FlatSymbol {
  name: string;
  kind: string;
  /** Ancestor chain (document symbols) or containerName (workspace symbols); "" when top-level. */
  container: string;
  /** Host path of the file the symbol lives in. */
  path: string;
  /** The nav target — the symbol name's 1-based range. */
  range: SymbolRange;
}

/** A ranked symbol carrying the fuzzy-match positions (indices into `name`) for highlighting. */
export interface ScoredSymbol {
  sym: FlatSymbol;
  positions?: Set<number>;
}

/**
 * The outcome of a symbol query: whether a provider actually answered, plus the rows. `providerAvailable: false`
 * is the honest "no language server / no provider for this file" signal — the omnibar renders it as such rather
 * than as an empty result (no silent empty, per the no-fallbacks rule).
 */
export interface SymbolQueryResult {
  providerAvailable: boolean;
  items: FlatSymbol[];
}

/** The symbol *querying* half — implemented over monaco's LSP providers in symbol-source.ts. */
export interface SymbolQuerySource {
  /** Document symbols of the file the editor is currently showing. */
  documentSymbols(): Promise<SymbolQueryResult>;
  /** Workspace symbols matching `query` — a live LSP round-trip; `signal` aborts a superseded query. */
  workspaceSymbols(query: string, signal: AbortSignal): Promise<SymbolQueryResult>;
}

/**
 * The full editor-owned symbol surface the omnibar drives: querying plus live preview navigation. The preview
 * half is implemented in the editor controller (it owns the tabs + the real editor); the query half comes from
 * symbol-source.ts. Preview reveals a symbol in the *real* editor as the selection moves — in place for a symbol
 * in the active file, or in a reused preview tab for another file — so you see the actual code with full
 * highlighting (what VS Code's Go-to-Symbol does), then commit (Enter) or restore (Esc).
 */
export interface SymbolActions extends SymbolQuerySource {
  /** Reveal `sym` in the real editor as a live preview (select in place, or open a preview tab for another file). */
  preview(sym: FlatSymbol): void;
  /** Restore the editor to its pre-preview state — the omnibar was dismissed without committing. */
  cancelPreview(): void;
  /** Keep the previewed location, promoting a cross-file preview tab so the jump sticks. */
  commitPreview(sym: FlatSymbol): void;
}

/** DFS-flattens a document-symbol tree into ordered rows, composing each symbol's dotted ancestor chain as `container`. */
export function flattenDocumentSymbols(
  nodes: readonly DocSymbolNode[],
  path: string,
): FlatSymbol[] {
  const out: FlatSymbol[] = [];
  const walk = (list: readonly DocSymbolNode[], container: string): void => {
    for (const node of list) {
      out.push({ name: node.name, kind: node.kind, container, path, range: node.selectionRange });
      if (node.children !== undefined && node.children.length > 0) {
        walk(node.children, container === "" ? node.name : `${container}.${node.name}`);
      }
    }
  };
  walk(nodes, "");
  return out;
}

/**
 * Fuzzy-ranks symbols by `name` (best-first, with highlight positions). An empty query returns the input order
 * unranked — document order for @, server-relevance order for # — mirroring the palette's empty-query behavior.
 */
export function rankSymbols(items: readonly FlatSymbol[], query: string): ScoredSymbol[] {
  const q = query.trim();
  if (q.length === 0) {
    return items.map((sym) => ({ sym }));
  }
  const fzf = new Fzf(items as FlatSymbol[], {
    selector: (s) => s.name,
    tiebreakers: [byLengthAsc],
    casing: "case-insensitive",
  });
  return fzf.find(q).map((r) => ({ sym: r.item, positions: r.positions }));
}
