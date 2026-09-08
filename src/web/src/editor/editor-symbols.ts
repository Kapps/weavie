import type { FlatSymbol, SymbolActions } from "../symbols/symbol-match";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import { samePath } from "./fs-path";
import type { NavLocation } from "./nav-history";
import { REVEAL_SCROLL } from "./reveal-scroll";

export const noEditorSymbols: SymbolActions = {
  documentSymbols: async () => ({ providerAvailable: false, items: [] }),
  workspaceSymbols: async () => ({ providerAvailable: false, items: [] }),
  preview: () => {},
  cancelPreview: () => {},
  commitPreview: () => {},
};

/** An omnibar interaction keeps the source binding and return location it captured on opening. */
export function createEditorSymbols(options: {
  connection: TextEditorConnection;
  origin: NavLocation;
  suspendHistory(): () => void;
  commit(origin: NavLocation, symbol: FlatSymbol): void;
}): SymbolActions {
  const { connection } = options;
  const { origin } = options;
  let resume: (() => void) | undefined;
  return {
    documentSymbols: () => connection.symbols.documentSymbols(),
    workspaceSymbols: (query, signal) => connection.symbols.workspaceSymbols(query, signal),
    preview: (symbol) => {
      if (!editorContexts.live(connection) || !samePath(origin.path, symbol.path)) return;
      resume ??= options.suspendHistory();
      connection.editor.setSelection(symbol.range);
      connection.editor.revealRangeInCenterIfOutsideViewport(symbol.range, REVEAL_SCROLL);
    },
    cancelPreview: () => {
      if (resume === undefined) return;
      if (editorContexts.live(connection)) connection.restore(origin);
      resume();
      resume = undefined;
    },
    commitPreview: (symbol) => {
      resume?.();
      resume = undefined;
      if (editorContexts.live(connection)) options.commit(origin, symbol);
    },
  };
}
