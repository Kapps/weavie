// The shape of a VS Code color theme: the JSON a theme extension contributes (e.g. one installed from
// Open VSX), and the form Weavie's built-in themes are authored in. A theme carries two independent
// color tables that layer at render time — `tokenColors` (TextMate grammar, syntactic) and
// `semanticTokenColors` (LSP server, semantic, painted on top) — plus the workbench `colors` map that
// drives the editor chrome, the terminal, and Weavie's own UI (spec §3, §5). This is the single shape
// the resolver, the Monaco re-theme, and the installed-theme converter all speak.

/** One TextMate token-color rule (syntactic coloring). */
export interface TokenColorRule {
  /** Human label — for readability in authored themes; ignored by the resolver. */
  name?: string;
  /** A single TextMate scope, or a list of scopes this rule paints. */
  scope?: string | string[];
  settings: {
    foreground?: string;
    background?: string;
    /** Space-separated subset of "italic bold underline strikethrough"; "" clears inherited styles. */
    fontStyle?: string;
  };
}

/** A semantic-token color: either a bare foreground hex or a small settings object. */
export type SemanticTokenColor = string | { foreground?: string; fontStyle?: string };

/** A VS Code color theme (the contributed/authored JSON). */
export interface VsCodeColorTheme {
  name: string;
  /** Base polarity; drives light/dark fallbacks for keys the theme omits. */
  type: "dark" | "light" | "hc" | "hcLight";
  /** Workbench color ids → hex string (e.g. `editor.background`). */
  colors: Record<string, string>;
  /** TextMate scope → color rules (syntactic). */
  tokenColors: TokenColorRule[];
  /** Semantic token selector → color (semantic, layered over `tokenColors`). */
  semanticTokenColors?: Record<string, SemanticTokenColor>;
  /** Whether semantic highlighting is applied on top of TextMate tokens. */
  semanticHighlighting?: boolean;
}
