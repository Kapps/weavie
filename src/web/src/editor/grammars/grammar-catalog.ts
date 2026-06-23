// Joins each tm-grammars grammar with linguist-languages file extensions (keyed on TextMate scope) into the
// languages registered with Monaco. Curated @codingame packs (TS/TSX, C#, Go) are excluded to avoid
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
  /** The tm-grammars grammar name (== file basename == the Monaco language id), e.g. "rust". */
  readonly name: string;
  /** TextMate scope the grammar declares, e.g. "source.rust". */
  readonly scopeName: string;
  /** Human-readable name (for the Monaco language registration / pickers). */
  readonly displayName: string;
  /** File extensions (".rs" form) that resolve a model to this language. */
  readonly extensions: readonly string[];
}

// Scopes owned by the curated @codingame packs (see vscode-services.ts) — never re-register these.
// JavaScript is intentionally not curated, so the broad loader fills that gap.
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
 * Builds the broad-highlighting catalog: every non-curated tm-grammars grammar with a linguist extension
 * match. Extensions are de-duplicated first-wins (curated pre-seeded), so no two languages claim the same one.
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
      scopeName: grammar.scopeName,
      displayName: grammar.displayName,
      extensions,
    });
  }
  return catalog;
}
