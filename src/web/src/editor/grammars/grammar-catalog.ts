// The data join that drives broad highlighting: pair each tm-grammars grammar (grammar file + scope) with
// the file-extension associations from linguist-languages, keyed on TextMate scope. tm-grammars ships ~250
// grammars but no file extensions; linguist ships extensions + the `tmScope` that matches a grammar's
// `scopeName`. The result is the set of languages we register with Monaco (id + extensions + scope), so an
// opened file resolves to the right language id and tokenizes with the right grammar.
//
// The curated @codingame packs (TS/TSX, C#, Go) stay authoritative — they ship full language-configuration
// and drive LSP selection — so their scopes/extensions are excluded here to avoid double-registration.

import type { Language } from "linguist-languages";
import { grammars } from "tm-grammars";

// Import linguist's per-language data files directly via glob rather than its barrel `index.js`: the
// barrel uses es2022 string-named exports (`export { default as '1C Enterprise' }`) that the dev server's
// esbuild (es2020 target) refuses to transform. Each `data/*.js` file is a clean `export default {...}`.
const linguistData = import.meta.glob<Language>(
  "../../../node_modules/linguist-languages/data/*.js",
  {
    eager: true,
    import: "default",
  },
);

/** One language to register for broad highlighting: a tm-grammars grammar joined with its file extensions. */
export interface BroadGrammar {
  /** The tm-grammars grammar name (== file basename == the Monaco language id we register), e.g. "rust". */
  readonly name: string;
  /** TextMate scope the grammar declares, e.g. "source.rust". */
  readonly scopeName: string;
  /** Human-readable name (for the Monaco language registration / pickers). */
  readonly displayName: string;
  /** File extensions (".rs" form) that resolve a model to this language. */
  readonly extensions: readonly string[];
}

// Scopes owned by the curated @codingame packs (see vscode-services.ts) — never re-register these.
// JavaScript is intentionally NOT curated, so the broad loader fills that gap.
const CURATED_SCOPES = new Set(["source.ts", "source.tsx", "source.cs", "source.go"]);
// Curated extensions, pre-seeded so a curated language always wins ownership of its extension.
const CURATED_EXTENSIONS = [
  ".ts",
  ".cts",
  ".mts",
  ".tsx",
  ".cs",
  ".csx",
  ".cake",
  ".go",
  ".tsbuildinfo",
];

interface ScopeExtensions {
  readonly extensions: Set<string>;
}

/**
 * Builds the broad-highlighting catalog: for every tm-grammars grammar with a linguist extension match
 * (and not owned by a curated pack), the language id + scope + the file extensions that resolve to it.
 * Extensions are de-duplicated first-wins (curated pre-seeded), so no two languages claim the same one.
 */
export function buildBroadCatalog(): BroadGrammar[] {
  // linguist: TextMate scope -> the union of file extensions of every language that maps to it.
  const byScope = new Map<string, ScopeExtensions>();
  for (const language of Object.values(linguistData)) {
    const scope = language?.tmScope;
    if (scope === undefined || scope === "none" || !language.extensions?.length) {
      continue;
    }
    let entry = byScope.get(scope);
    if (entry === undefined) {
      entry = { extensions: new Set() };
      byScope.set(scope, entry);
    }
    for (const extension of language.extensions) {
      entry.extensions.add(extension);
    }
  }

  const claimed = new Set<string>(CURATED_EXTENSIONS);
  const catalog: BroadGrammar[] = [];
  for (const grammar of grammars) {
    if (CURATED_SCOPES.has(grammar.scopeName)) {
      continue;
    }
    const linguist = byScope.get(grammar.scopeName);
    if (linguist === undefined) {
      continue; // no file extension -> a file could never resolve to it; skip
    }
    const extensions = [...linguist.extensions].filter((extension) => !claimed.has(extension));
    if (extensions.length === 0) {
      continue; // every extension already claimed -> skip to avoid conflicts
    }
    for (const extension of extensions) {
      claimed.add(extension);
    }
    catalog.push({
      name: grammar.name,
      scopeName: grammar.scopeName,
      displayName: grammar.displayName,
      extensions,
    });
  }
  return catalog;
}
