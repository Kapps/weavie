import MarkdownIt from "markdown-it";
import type Token from "markdown-it/lib/token.mjs";

const markdown = new MarkdownIt({ html: false, typographer: true });

/** A plain first-line preview; the disclosure retains the complete original markdown. */
export function thoughtPreview(text: string): string {
  const firstLine = text.trimStart().split("\n", 1)[0] ?? "";
  return tokenText(markdown.parse(firstLine, {})).trim();
}

function tokenText(tokens: Token[]): string {
  return tokens
    .map((token) => (token.children ? tokenText(token.children) : token.content))
    .join("");
}
