// Joins each tm-grammars grammar with linguist-languages file extensions (keyed on TextMate scope) into the
// languages registered with Monaco. Curated @codingame packs (TS/TSX, C#, Go, Python, Rust) are excluded to avoid
// double-registration, since they ship full language-configuration and drive LSP selection.

import type { Language } from "linguist-languages";
import { grammars } from "tm-grammars";

// Import linguist's per-language data files via glob, not its barrel `index.js`: the barrel's es2022
// string-named exports break the dev server's es2020 esbuild target. Each `data/*.js` is a clean default export.
const linguistData = import.meta.glob<Language>(
  "../../../node_modules/linguist-languages/data/*.js",
  {
    eager: true,
    import: "default",
  },
);

/** One language to register for broad highlighting: a tm-grammars grammar joined with its file extensions. */
export interface BroadGrammar {
  /** The tm-grammars grammar name (and file basename), e.g. "rust". */
  readonly name: string;
  /** Monaco language id the extensions resolve to; curated scopes reuse the package's existing id. */
  readonly languageId: string;
  /** TextMate scope the grammar declares, e.g. "source.rust". */
  readonly scopeName: string;
  /** Human-readable name (for the Monaco language registration / pickers). */
  readonly displayName: string;
  /** File extensions (".rs" form) that resolve a model to this language. */
  readonly extensions: readonly string[];
  /** Whether this entry owns and must register the grammar, rather than only contributing extensions. */
  readonly registerGrammar: boolean;
}

// Scopes whose grammar/config comes from a curated @codingame pack. Any additional Linguist extensions that
// share one of these scopes are contributed to the existing language id without re-registering its grammar.
const CURATED_SCOPE_LANGUAGES = new Map([
  ["source.ts", "typescript"],
  ["source.tsx", "typescriptreact"],
  ["source.cs", "csharp"],
  ["source.go", "go"],
  ["source.python", "python"],
  ["source.rust", "rust"],
]);

interface ScopeExtensions {
  readonly extensions: Set<string>;
}

/**
 * Builds the broad-highlighting catalog: every non-curated tm-grammars grammar with a linguist extension
 * match. Extensions are de-duplicated first-wins (curated pre-seeded), so no two languages claim the same one.
 */
export function buildBroadCatalog(claimedExtensions: ReadonlySet<string>): BroadGrammar[] {
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

  const claimed = new Set<string>(claimedExtensions);
  const catalog: BroadGrammar[] = [];
  for (const grammar of grammars) {
    const linguist = byScope.get(grammar.scopeName);
    if (linguist === undefined) {
      continue; // no file extension -> a file could never resolve to it
    }
    const extensions = [...linguist.extensions].filter((extension) => !claimed.has(extension));
    if (extensions.length === 0) {
      continue; // every extension already claimed -> avoid conflicts
    }
    for (const extension of extensions) {
      claimed.add(extension);
    }
    catalog.push({
      name: grammar.name,
      languageId: CURATED_SCOPE_LANGUAGES.get(grammar.scopeName) ?? grammar.name,
      scopeName: grammar.scopeName,
      displayName: grammar.displayName,
      extensions,
      registerGrammar: !CURATED_SCOPE_LANGUAGES.has(grammar.scopeName),
    });
  }
  return catalog;
}
