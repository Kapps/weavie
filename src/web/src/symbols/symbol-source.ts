// Monaco glue for the omnibar's symbol modes: queries the registered LSP providers and maps their results into
// the pure FlatSymbol rows (flattened + ranked in symbol-match.ts). Imports monaco, so it lands in the lazily
// loaded editor chunk and is reached ONLY via the editor controller's dynamic import — never from the omnibar's
// static graph. Document symbols use ILanguageFeaturesService (the provider test-symbols.ts already queries);
// workspace symbols use the search-contrib WorkspaceSymbolProviderRegistry (VS Code's Ctrl+T path).

import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { ILanguageFeaturesService } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/services/languageFeatures.service";
import {
  getWorkspaceSymbols,
  WorkspaceSymbolProviderRegistry,
} from "@codingame/monaco-vscode-api/vscode/vs/workbench/contrib/search/common/search";
import { CancellationToken } from "vscode-jsonrpc";
import { uriHostPath } from "../editor/fs-path";
import { monaco } from "../editor/monaco-setup";
import {
  type DocSymbolNode,
  type FlatSymbol,
  flattenDocumentSymbols,
  type SymbolQueryResult,
  type SymbolQuerySource,
} from "./symbol-match";

// Semantic label per monaco SymbolKind, keyed off the enum (not raw numbers) so it can't drift with the encoding.
// The omnibar maps these to icons and falls back to a generic glyph for anything not listed.
function kindLabel(kind: monaco.languages.SymbolKind): string {
  const K = monaco.languages.SymbolKind;
  switch (kind) {
    case K.Class:
      return "class";
    case K.Interface:
      return "interface";
    case K.Struct:
      return "struct";
    case K.Enum:
      return "enum";
    case K.EnumMember:
      return "enum-member";
    case K.Method:
      return "method";
    case K.Constructor:
      return "constructor";
    case K.Function:
      return "function";
    case K.Property:
      return "property";
    case K.Field:
      return "field";
    case K.Variable:
      return "variable";
    case K.Constant:
      return "constant";
    case K.Module:
    case K.Namespace:
    case K.Package:
      return "module";
    case K.TypeParameter:
      return "type";
    case K.Event:
      return "event";
    case K.File:
      return "file";
    default:
      return "symbol";
  }
}

function toNode(symbol: monaco.languages.DocumentSymbol): DocSymbolNode {
  const node: DocSymbolNode = {
    name: symbol.name,
    kind: kindLabel(symbol.kind),
    selectionRange: symbol.selectionRange,
  };
  if (symbol.children !== undefined) {
    node.children = symbol.children.map(toNode);
  }
  return node;
}

/**
 * Builds the symbol *querying* surface over `editor`. Both queries return `providerAvailable: false` when no LSP
 * provider is registered (the honest "no language server for this file" signal, not a silent empty). Preview
 * navigation is layered on top by the editor controller (it owns the tabs + the real editor).
 */
export function createSymbolSource(editor: monaco.editor.IStandaloneCodeEditor): SymbolQuerySource {
  const documentSymbols = async (): Promise<SymbolQueryResult> => {
    const model = editor.getModel();
    if (model === null || model.uri.scheme !== "file") {
      return { providerAvailable: false, items: [] };
    }
    const providers =
      StandaloneServices.get(ILanguageFeaturesService).documentSymbolProvider.ordered(model);
    if (providers.length === 0) {
      return { providerAvailable: false, items: [] };
    }
    const path = uriHostPath(model.uri);
    for (const provider of providers) {
      const symbols = await provider.provideDocumentSymbols(model, CancellationToken.None);
      if (symbols != null && symbols.length > 0) {
        return {
          providerAvailable: true,
          items: flattenDocumentSymbols(symbols.map(toNode), path),
        };
      }
    }
    return { providerAvailable: true, items: [] };
  };

  const workspaceSymbols = async (
    query: string,
    signal: AbortSignal,
  ): Promise<SymbolQueryResult> => {
    if (WorkspaceSymbolProviderRegistry.all().length === 0) {
      return { providerAvailable: false, items: [] };
    }
    const source = new monaco.CancellationTokenSource();
    signal.addEventListener("abort", () => source.cancel(), { once: true });
    const found = await getWorkspaceSymbols(query, source.token);
    const items: FlatSymbol[] = found.map(({ symbol }) => ({
      name: symbol.name,
      kind: kindLabel(symbol.kind),
      container: symbol.containerName ?? "",
      path: uriHostPath(symbol.location.uri),
      range: symbol.location.range,
    }));
    return { providerAvailable: true, items };
  };

  return { documentSymbols, workspaceSymbols };
}
