// Pure text → renderable model for comment-prose.ts: finds the comment blocks worth rendering (multi-line runs
// + doc comments), strips markers, and parses each line into inline runs (text + `code` chips + Markdown
// emphasis), preserving the author's line breaks. No DOM or Monaco here — a deterministic, side-effect-free transform.

import MarkdownIt from "markdown-it";

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

/** A run of plain comment text, optionally carrying Markdown emphasis (bold / italic / strikethrough). */
export type TextRun = { text: string; strong?: boolean; em?: boolean; strike?: boolean };

/** One inline run inside a rendered comment line: text (optionally emphasised) or an inline-code chip. */
export type Inline = TextRun | { code: string };

// Comment syntax per Monaco language id; the default ("//" + "/* */") covers the C-family + JS/TS/CSS/Go/Rust.
// An unknown language falls back to the default, which is harmless.
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
 * Finds every full-line comment block in `lines` (line-comment runs, block comments, and JSX `{/* … *​/}`),
 * each carrying its 1-based inclusive range, a `doc` flag, and marker-stripped text. Trailing comments (after
 * code on the same line) are skipped; the caller picks which to render via the `editor.commentProse` mode.
 */
export function scanCommentBlocks(lines: string[], syntax: CommentSyntax): CommentBlock[] {
  const blocks: CommentBlock[] = [];

  // The 0-based line that closes a block opened at `i`. On the opening line only a close AFTER the opener
  // counts; otherwise the first later line with the close marker, falling back to EOF if unterminated.
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

    // A block comment ( /* … */ ), or in JSX an expression-container comment ( {/* … */} ), whose opener starts
    // the line. A delimiter after code is a trailing comment (line doesn't start with it) and left as code.
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

// XML-doc inline code elements as one matchAll tokeniser: (1) `<c>`/`<code>` verbatim content; (2) `<see cref>`
// → last dotted segment; (3) any self-closing single-attribute element → its attribute value. Each becomes a
// code run taken VERBATIM (never round-tripped through backticks), so a delimiter in the code can't corrupt the line.
const XML_CODE =
  /<(c|code)>([\s\S]*?)<\/\1>|<see\s+cref="(?:[A-Za-z]:)?([^"]+)"\s*\/?>|<[A-Za-z][\w.-]*\s+[\w.-]+="([^"]*)"\s*\/>/gi;

// Strip leftover/unknown tags from a plain-text gap, looping until stable so a nested tag (`<<x>script>`)
// can't survive one pass. Cosmetic only — runs go in via textContent, never innerHTML, so nothing executes.
function stripTags(text: string): string {
  let prev: string;
  let out = text;
  do {
    prev = out;
    out = out.replace(/<[^>]*>/g, "");
  } while (out !== prev);
  return out;
}

// XML doc comments → inline runs for one source line: structural wrappers dropped, `<param>`/`<typeparam>`/
// `<returns>` to a labelled text shape, XML_CODE elements to code runs, the rest plain text. Best-effort, line-by-line.
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

// markdown-it's inline tokenizer, with block parsing irrelevant (we only call parseInline) and links/images/raw
// HTML left literal so only emphasis, `code`, and ~~strike~~ are lifted. CommonMark emphasis rules matter here:
// an intraword `_` (a `snake_case` identifier) is never treated as italics, which a naive split would botch.
const inlineMd = new MarkdownIt({ html: false, linkify: false }).disable([
  "link",
  "image",
  "autolink",
  "html_inline",
  "entity",
]);

// Split a line into inline runs: `backtick` spans become code chips and Markdown emphasis becomes marked text
// runs. We read only each token's raw `.content` (never its HTML), so nothing here can reach innerHTML.
function parseInline(text: string): Inline[] {
  const runs: Inline[] = [];
  let strong = 0;
  let em = 0;
  let strike = 0;
  for (const token of inlineMd.parseInline(text, {})[0]?.children ?? []) {
    if (token.type === "code_inline") {
      runs.push({ code: token.content });
      continue;
    }
    if (token.type.endsWith("_open") || token.type.endsWith("_close")) {
      const delta = token.type.endsWith("_open") ? 1 : -1;
      if (token.type.startsWith("strong")) strong += delta;
      else if (token.type.startsWith("em")) em += delta;
      else if (token.type.startsWith("s_")) strike += delta;
      continue;
    }
    if (token.content !== "") {
      const run: TextRun = { text: token.content };
      if (strong > 0) run.strong = true;
      if (em > 0) run.em = true;
      if (strike > 0) run.strike = true;
      runs.push(run);
    }
  }
  return runs.length > 0 ? runs : [{ text: "" }];
}

/**
 * Parses marker-stripped `content` line-for-line into inline runs (text + `code` chips), preserving line
 * breaks. `xmlDoc` uses `parseXmlDocLine`; otherwise `parseInline` lifts `` `backtick` `` spans and Markdown emphasis.
 */
export function parseCommentLines(content: string[], xmlDoc: boolean): Inline[][] {
  return content.map((line) => (xmlDoc ? parseXmlDocLine(line) : parseInline(line)));
}
