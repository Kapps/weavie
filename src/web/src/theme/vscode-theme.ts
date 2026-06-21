// The shape of a VS Code color theme: the JSON a theme extension contributes (e.g. from Open VSX) and the
// form Weavie's built-ins are authored in. A theme carries two color tables that layer at render time —
// `tokenColors` (TextMate, syntactic) and `semanticTokenColors` (LSP, painted on top) — plus the
// workbench `colors` map driving editor chrome, terminal, and Weavie's UI (spec §3, §5). The single shape
// the resolver, the Monaco re-theme, and the installed-theme converter all speak.

/** One TextMate token-color rule (syntactic coloring). */
export interface TokenColorRule {
  /** Human label for readability; ignored by the resolver. */
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
