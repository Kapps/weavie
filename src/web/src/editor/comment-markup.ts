// Pure text → renderable model for the comment-prose renderer (comment-prose.ts). Given a model's raw lines
// and a language id, finds the comment blocks worth rendering as prose (multi-line runs + doc comments),
// strips their markers, and parses each line into inline runs (plain text + inline `code` chips) — preserving
// the author's line breaks exactly, never reflowing them into paragraphs. No DOM and no Monaco here —
// comment-prose.ts owns rendering + the editor — so this stays a deterministic, side-effect-free transform.

/** A language's comment delimiters. `line` prefixes are matched longest-first so `///` wins over `//`. */
export interface CommentSyntax {
  line: string[];
  block?: readonly [open: string, close: string];
  /** Doc comments use XML doc tags (`<summary>`, `<c>`, `<param>`) to lift into prose — C#, VB, F#. */
  xmlDoc?: boolean;
  /** JSX/TSX brace-wrapped expression-container block comments are recognised + rendered as comments too. */
  jsxComment?: boolean;
}

/** A comment span worth rendering as prose: its 1-based inclusive line range + the marker-stripped text. */
export interface CommentBlock {
  startLine: number;
  endLine: number;
  /** A doc comment (`/** … *​/`, `///`, `//!`): rendered even when it's a single line. */
  doc: boolean;
  /** Comment text with the comment markers (and any `*` gutter) removed, one entry per source line. */
  content: string[];
}

/** One inline run inside a rendered comment line: plain text or an inline-code chip. */
export type Inline = { text: string } | { code: string };

// Comment syntax per Monaco language id. The default ("//" + "/* */") covers the C-family + JS/TS/CSS/Go/Rust;
// the rest override line/block as needed. Only languages whose comments we can strip cleanly are listed; an
// unknown language falls back to the default, which is harmless (a non-comment line never starts with these).
const DEFAULT_SYNTAX: CommentSyntax = { line: ["//"], block: ["/*", "*/"] };
const SYNTAX: Record<string, CommentSyntax> = {
  python: { line: ["#"] },
  shellscript: { line: ["#"] },
  ruby: { line: ["#"] },
  yaml: { line: ["#"] },
  toml: { line: ["#"] },
  dockerfile: { line: ["#"] },
  makefile: { line: ["#"] },
  r: { line: ["#"] },
  perl: { line: ["#"] },
  lua: { line: ["--"], block: ["--[[", "]]"] },
  sql: { line: ["--"] },
  haskell: { line: ["--"] },
  html: { block: ["<!--", "-->"], line: [] },
  xml: { block: ["<!--", "-->"], line: [] },
  // C-family doc comments use a `///` (and Rust `//!`) line prefix; list them first so they're detected as doc.
  rust: { line: ["//!", "///", "//"], block: ["/*", "*/"] },
  csharp: { line: ["///", "//"], block: ["/*", "*/"], xmlDoc: true },
  cpp: { line: ["///", "//"], block: ["/*", "*/"] },
  c: { line: ["///", "//"], block: ["/*", "*/"] },
  java: { line: ["///", "//"], block: ["/*", "*/"] },
  go: { line: ["//"], block: ["/*", "*/"] },
  // JSX dialects: a `{/* … */}` expression-container comment is the idiomatic way to comment inside markup.
  typescriptreact: { line: ["//"], block: ["/*", "*/"], jsxComment: true },
  javascriptreact: { line: ["//"], block: ["/*", "*/"], jsxComment: true },
};

/** The comment syntax for a Monaco language id (falls back to the C-family default). */
export function commentSyntaxFor(languageId: string): CommentSyntax {
  return SYNTAX[languageId] ?? DEFAULT_SYNTAX;
}

// A line is a line-comment if, ignoring leading whitespace, it starts with one of the prefixes. Returns the
// matched prefix (longest-first wins) or undefined.
function matchLinePrefix(line: string, prefixes: readonly string[]): string | undefined {
  const trimmed = line.trimStart();
  for (const prefix of prefixes) {
    if (prefix.length > 0 && trimmed.startsWith(prefix)) {
      return prefix;
    }
  }
  return undefined;
}

// Strip a line-comment prefix and a single following space from one line.
function stripLinePrefix(line: string, prefix: string): string {
  const trimmed = line.trimStart();
  const rest = trimmed.slice(prefix.length);
  return rest.startsWith(" ") ? rest.slice(1) : rest;
}

// Strip block delimiters + the JSDoc-style `*` gutter from a block comment's raw lines.
function stripBlock(raw: string[], open: string, close: string): string[] {
  const out = [...raw];
  out[0] = out[0]!.trimStart();
  if (out[0].startsWith(open)) {
    out[0] = out[0].slice(open.length);
  }
  const lastIdx = out.length - 1;
  const closeAt = out[lastIdx]!.lastIndexOf(close);
  if (closeAt !== -1) {
    out[lastIdx] = out[lastIdx]!.slice(0, closeAt);
  }
  // Drop a leading ` * ` star gutter (and trailing whitespace) on every line.
  return out.map((line) => line.replace(/^\s*\*?\s?/, "").replace(/\s+$/, ""));
}

/**
 * Finds every full-line comment block in `lines`: runs of consecutive line-comments, block comments
 * (`/* … *​/`), and — for JSX dialects — `{/* … *​/}` comments. Each carries its 1-based inclusive line range,
 * a `doc` flag (a doc-marker block / an all-`///`-or-`//!` run), and the marker-stripped text. Trailing
 * comments (after code on the same line, so the line doesn't start with the marker) are skipped. The caller
 * decides which of these to render via the `editor.commentProse` mode (none / documentation / multi-line / all).
 */
export function scanCommentBlocks(lines: string[], syntax: CommentSyntax): CommentBlock[] {
  const blocks: CommentBlock[] = [];

  // The 0-based index of the line that closes a block opened at `i` by `open`/`close`. On the opening line
  // only a close AFTER the opener counts (so a one-line block closes itself); otherwise walk down to the first
  // line containing the close marker, falling back to EOF for an unterminated block.
  const blockEnd = (i: number, open: string, close: string): number => {
    const openEnd = lines[i]!.indexOf(open) + open.length;
    if (lines[i]!.indexOf(close, openEnd) !== -1) {
      return i;
    }
    let j = i + 1;
    while (j < lines.length && !lines[j]!.includes(close)) {
      j++;
    }
    return Math.min(j, lines.length - 1);
  };

  let i = 0;
  while (i < lines.length) {
    const line = lines[i]!;
    const trimmed = line.trimStart();

    // A block comment ( /* … */ ) or, in JSX, an expression-container comment ( {/* … */} ) whose opener
    // starts the line. A delimiter after code on the same line is a trailing comment — the line wouldn't start
    // with it — so it's left as code.
    const block: readonly [string, string] | undefined =
      syntax.block !== undefined && trimmed.startsWith(syntax.block[0])
        ? syntax.block
        : syntax.jsxComment === true && trimmed.startsWith("{/*")
          ? ["{/*", "*/}"]
          : undefined;
    if (block !== undefined) {
      const [open, close] = block;
      const end = blockEnd(i, open, close);
      // A doc block opens with the doc marker (`/**`); JSX comments are never doc comments.
      const doc = open !== "{/*" && trimmed.startsWith(`${open}*`);
      blocks.push({
        startLine: i + 1,
        endLine: end + 1,
        doc,
        content: stripBlock(lines.slice(i, end + 1), open, close),
      });
      i = end + 1;
      continue;
    }

    // A run of consecutive whole-line comments.
    const prefix = matchLinePrefix(line, syntax.line);
    if (prefix !== undefined) {
      let j = i;
      const run: string[] = [];
      let doc = true;
      while (j < lines.length) {
        const p = matchLinePrefix(lines[j]!, syntax.line);
        if (p === undefined) {
          break;
        }
        run.push(stripLinePrefix(lines[j]!, p));
        // The run is a doc comment only if every line uses a doc prefix (`///` / `//!`).
        doc = doc && (p.startsWith("///") || p === "//!");
        j++;
      }
      blocks.push({ startLine: i + 1, endLine: j, doc, content: run });
      i = j;
      continue;
    }

    i++;
  }
  return blocks;
}

// The XML-doc inline code elements, as one tokeniser regex (used with matchAll). Three alternatives:
//  1. `<c>…</c>` / `<code>…</code>` — verbatim code content.
//  2. `<see cref="…"/>` — a symbol reference; rendered as its last dotted segment.
//  3. any self-closing single-attribute element (`<paramref name="x"/>`, `<typeparamref name="T"/>`,
//     `<see langword="null"/>`) — its attribute value.
// Each becomes a code RUN whose text is taken VERBATIM — never round-tripped through a backtick-delimited
// string — so code containing the delimiter (a chord like `<c>ctrl+\`</c>`) can't corrupt the rest of the line.
const XML_CODE =
  /<(c|code)>([\s\S]*?)<\/\1>|<see\s+cref="(?:[A-Za-z]:)?([^"]+)"\s*\/?>|<[A-Za-z][\w.-]*\s+[\w.-]+="([^"]*)"\s*\/>/gi;

// Strip any leftover/unknown tags from a plain-text gap, looping until stable so a crafted nested tag (e.g.
// `<<x>script>`) can't survive a single pass. Cosmetic only — every run is inserted via textContent (never
// innerHTML), so no markup can execute regardless; this just keeps stray angle-bracket noise out of the text.
function stripTags(text: string): string {
  let prev: string;
  let out = text;
  do {
    prev = out;
    out = out.replace(/<[^>]*>/g, "");
  } while (out !== prev);
  return out;
}

// C# (and other) XML doc comments → inline runs for one source line. Structural wrappers (`<summary>` …) are
// dropped, `<param>`/`<typeparam>`/`<returns>` become a labelled text shape, the inline code elements above
// become code runs (content verbatim), and everything else is plain text. Best-effort and line-by-line, to
// match the line-faithful renderer.
function parseXmlDocLine(line: string): Inline[] {
  let s = line.replace(/<\/?(summary|remarks|para|example|value)>/gi, "");
  s = s.replace(
    /<(param|typeparam)\s+name="([^"]+)"\s*>([\s\S]*?)<\/\1>/gi,
    (_m, _tag, name, desc) => `- <c>${name}</c> — ${String(desc).trim()}`,
  );
  s = s.replace(
    /<returns>([\s\S]*?)<\/returns>/gi,
    (_m, desc) => `Returns: ${String(desc).trim()}`,
  );

  const runs: Inline[] = [];
  const pushText = (text: string): void => {
    const cleaned = stripTags(text);
    if (cleaned !== "") {
      runs.push({ text: cleaned });
    }
  };
  let last = 0;
  for (const match of s.matchAll(XML_CODE)) {
    const at = match.index ?? 0;
    pushText(s.slice(last, at));
    const cref = match[3];
    const code =
      match[1] !== undefined
        ? match[2]!
        : cref !== undefined
          ? (cref.split(".").pop() ?? cref)
          : match[4]!;
    runs.push({ code });
    last = at + match[0].length;
  }
  pushText(s.slice(last));

  // Mirror the old per-line trimEnd: drop trailing whitespace on the final text run.
  const tail = runs[runs.length - 1];
  if (tail !== undefined && "text" in tail) {
    tail.text = tail.text.replace(/\s+$/, "");
    if (tail.text === "") {
      runs.pop();
    }
  }
  return runs.length > 0 ? runs : [{ text: "" }];
}

// Split a line of text into inline runs, turning `backtick` spans into code chips. Parts alternate
// text/code/text…; an odd-index part is code unless it's the trailing part of an unterminated span (an odd
// number of backticks), in which case the backtick is literal text.
function parseInline(text: string): Inline[] {
  const parts = text.split("`");
  const runs: Inline[] = [];
  for (let k = 0; k < parts.length; k++) {
    const part = parts[k]!;
    const isCode = k % 2 === 1 && k < parts.length - 1;
    if (isCode) {
      runs.push({ code: part });
    } else if (k % 2 === 1) {
      // Unterminated span: keep the literal backtick so no text is lost.
      runs.push({ text: `\`${part}` });
    } else if (part.length > 0) {
      runs.push({ text: part });
    }
  }
  return runs.length > 0 ? runs : [{ text: "" }];
}

/**
 * Renders marker-stripped comment `content` line-for-line: each source line becomes one array of inline runs
 * (plain text + inline `code` chips), preserving the author's line breaks exactly rather than reflowing them
 * into paragraphs. `xmlDoc` parses C#-style XML doc tags structurally (`parseXmlDocLine`); otherwise the line
 * is split on author `` `backtick` `` spans (`parseInline`). The renderer sizes each rendered line to one
 * editor line, so the styled view occupies exactly the raw comment's footprint.
 */
export function parseCommentLines(content: string[], xmlDoc: boolean): Inline[][] {
  return content.map((line) => (xmlDoc ? parseXmlDocLine(line) : parseInline(line)));
}
