// Pure text → prose model for the comment-prose renderer (comment-prose.ts). Given a model's raw lines and a
// language id, finds the comment blocks worth rendering as prose (multi-line runs + doc comments), strips
// their markers, and parses the remaining text into a tiny markdown-ish model: paragraphs, ordered/unordered
// lists, and inline `code` chips. No DOM and no Monaco here — comment-prose.ts owns rendering + the editor —
// so this stays a deterministic, side-effect-free transform.

/** A language's comment delimiters. `line` prefixes are matched longest-first so `///` wins over `//`. */
export interface CommentSyntax {
  line: string[];
  block?: readonly [open: string, close: string];
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

/** One inline run inside a prose paragraph / list item: plain text or an inline-code chip. */
export type Inline = { text: string } | { code: string };

/** A parsed prose block: a paragraph or a list. List items each hold their own inline runs. */
export type ProseBlock =
  | { kind: "p"; runs: Inline[] }
  | { kind: "ul"; items: Inline[][] }
  | { kind: "ol"; start: number; items: Inline[][] };

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
  csharp: { line: ["///", "//"], block: ["/*", "*/"] },
  cpp: { line: ["///", "//"], block: ["/*", "*/"] },
  c: { line: ["///", "//"], block: ["/*", "*/"] },
  java: { line: ["///", "//"], block: ["/*", "*/"] },
  go: { line: ["//"], block: ["/*", "*/"] },
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
 * Finds the comment blocks in `lines` worth rendering as prose: runs of ≥2 consecutive line-comments, any
 * doc-comment run, and block comments that span ≥2 lines or open with a doc marker. Trailing comments (after
 * code on the same line) and lone non-doc single-line comments are skipped. Ranges are 1-based inclusive.
 */
export function scanCommentBlocks(lines: string[], syntax: CommentSyntax): CommentBlock[] {
  const blocks: CommentBlock[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i]!;
    const trimmed = line.trimStart();

    // Block comment whose opener is at the start of the line (a `/*` after code is a trailing comment we skip).
    if (syntax.block !== undefined && trimmed.startsWith(syntax.block[0])) {
      const [open, close] = syntax.block;
      // Find the line that closes the block. On the opening line only a close AFTER the opener counts (so a
      // one-line `/* … *​/` closes itself); otherwise walk down to the first line containing the close marker,
      // falling back to EOF for an unterminated block.
      const openEnd = line.indexOf(open) + open.length;
      let end: number;
      if (line.indexOf(close, openEnd) !== -1) {
        end = i;
      } else {
        let j = i + 1;
        while (j < lines.length && !lines[j]!.includes(close)) {
          j++;
        }
        end = Math.min(j, lines.length - 1);
      }
      const doc = trimmed.startsWith(`${open}*`); // e.g. `/**`
      if (end > i || doc) {
        blocks.push({
          startLine: i + 1,
          endLine: end + 1,
          doc,
          content: stripBlock(lines.slice(i, end + 1), open, close),
        });
      }
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
      const length = j - i;
      if (length >= 2 || doc) {
        blocks.push({ startLine: i + 1, endLine: j, doc, content: run });
      }
      i = j;
      continue;
    }

    i++;
  }
  return blocks;
}

// C# (and other) XML doc comments: lift the common tags into prose. Structural tags become paragraph breaks,
// `<c>`/`<code>` and `<see cref>` become inline code, `<param>`/`<returns>`/`<typeparam>` become labelled
// lines, and any remaining tags are dropped. Best-effort: unrecognised XML is left as readable text.
function xmlDocToProse(lines: string[]): string[] {
  return lines.map((raw) => {
    let line = raw;
    line = line.replace(/<\/?(summary|remarks|para|example|value)>/gi, "");
    line = line.replace(/<(c|code)>(.*?)<\/\1>/gi, (_m, _tag, code) => `\`${code}\``);
    line = line.replace(/<see\s+cref="(?:[A-Za-z]:)?([^"]+)"\s*\/?>/gi, (_m, ref) => {
      const name = String(ref).split(".").pop() ?? String(ref);
      return `\`${name}\``;
    });
    line = line.replace(
      /<(param|typeparam)\s+name="([^"]+)"\s*>(.*?)<\/\1>/gi,
      (_m, _tag, name, desc) => `- \`${name}\` — ${String(desc).trim()}`,
    );
    line = line.replace(
      /<returns>(.*?)<\/returns>/gi,
      (_m, desc) => `Returns: ${String(desc).trim()}`,
    );
    // Drop any leftover tags, looping until stable so a crafted nested tag (e.g. `<<x>script>`) can't survive a
    // single pass and leave a tag behind. The parsed text is only ever inserted as textContent (never
    // innerHTML), but a complete strip keeps it robust regardless.
    let prev: string;
    do {
      prev = line;
      line = line.replace(/<[^>]*>/g, "");
    } while (line !== prev);
    return line.trimEnd();
  });
}

// Split a line of text into inline runs, turning `backtick` spans into code chips. Parts alternate
// text/code/text…; an odd-index part is code unless it's the trailing part of an unterminated span (an odd
// number of backticks), in which case the backtick is literal text.
function parseInline(text: string): Inline[] {
  const parts = text.split("`");
  const runs: Inline[] = [];
  for (let k = 0; k < parts.length; k++) {
    const part = parts[k]!;
    const isCode = k % 2 === 1 && k !== parts.length - 1;
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

const ORDERED = /^(\d+)[.)]\s+(.*)$/;
const UNORDERED = /^[-*]\s+(.*)$/;

/**
 * Parses marker-stripped comment `content` into a prose model: blank lines separate paragraphs, `1.`/`-`
 * lines form lists, and everything else is paragraph text (consecutive lines joined with a space so prose
 * reflows). `xmlDoc` first lifts C#-style XML doc tags into the same markdown-ish shape.
 */
export function parseProse(content: string[], xmlDoc = false): ProseBlock[] {
  const lines = xmlDoc ? xmlDocToProse(content) : content;
  const blocks: ProseBlock[] = [];
  let para: string[] = [];
  let list: { kind: "ul" | "ol"; start: number; items: Inline[][] } | undefined;

  const flushPara = (): void => {
    if (para.length > 0) {
      blocks.push({ kind: "p", runs: parseInline(para.join(" ")) });
      para = [];
    }
  };
  const flushList = (): void => {
    if (list !== undefined) {
      blocks.push(
        list.kind === "ol"
          ? { kind: "ol", start: list.start, items: list.items }
          : { kind: "ul", items: list.items },
      );
      list = undefined;
    }
  };

  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed.length === 0) {
      flushPara();
      flushList();
      continue;
    }
    const ordered = ORDERED.exec(trimmed);
    const unordered = UNORDERED.exec(trimmed);
    if (ordered !== null) {
      flushPara();
      if (list?.kind !== "ol") {
        flushList();
        list = { kind: "ol", start: Number.parseInt(ordered[1]!, 10), items: [] };
      }
      list.items.push(parseInline(ordered[2]!));
    } else if (unordered !== null) {
      flushPara();
      if (list?.kind !== "ul") {
        flushList();
        list = { kind: "ul", start: 1, items: [] };
      }
      list.items.push(parseInline(unordered[1]!));
    } else {
      flushList();
      para.push(trimmed);
    }
  }
  flushPara();
  flushList();
  return blocks;
}
