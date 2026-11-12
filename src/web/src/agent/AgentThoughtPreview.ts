import MarkdownIt from "markdown-it";
import type Token from "markdown-it/lib/token.mjs";

const markdown = new MarkdownIt({ html: false, typographer: true });

/** A plain first-line preview; the disclosure retains the complete original markdown. */
export function thoughtPreview(text: string): string {
  const firstLine = text.trimStart().split("\n", 1)[0] ?? "";
  return tokenText(markdown.parse(firstLine, {})).trim();
}

/** Providers can append multiple summary sections to a single reasoning item. */
export function currentThoughtPreview(text: string): string {
  const tokens = markdown.parse(text, {});
  let heading = "";
  let latest = "";
  for (const [index, token] of tokens.entries()) {
    if (token.type !== "inline") continue;
    const plain = tokenText(token.children ?? []).trim();
    if (!plain) continue;
    latest = plain;
    const children = token.children?.filter(
      (child) => child.type !== "text" || child.content.trim(),
    );
    if (
      tokens[index - 1]?.type === "heading_open" ||
      (children?.[0]?.type === "strong_open" &&
        children.at(-1)?.type === "strong_close" &&
        children.slice(1, -1).every((child) => child.level > 0))
    )
      heading = plain;
  }
  return (heading || latest).replace(/\s+/g, " ");
}

function tokenText(tokens: Token[]): string {
  return tokens
    .map((token) =>
      token.children
        ? tokenText(token.children)
        : token.type === "softbreak" || token.type === "hardbreak"
          ? " "
          : token.content,
    )
    .join("");
}
