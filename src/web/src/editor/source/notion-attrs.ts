// Attribute helpers shared by the Notion enhanced-markdown parser (notion-parse.ts) and the edit path
// (notion-edit.ts): color-class mapping, a block's trailing `{…}` attrs, and custom-tag `key="value"` attrs.

const COLORS = new Set([
  "gray",
  "brown",
  "orange",
  "yellow",
  "green",
  "blue",
  "purple",
  "pink",
  "red",
]);

/** Maps a Notion enhanced-markdown color ("blue" → text, "blue_bg" → background) to our stylesheet class, or null. */
export function notionColorClass(color: string): string | null {
  const c = color.trim().toLowerCase();
  const bg = c.endsWith("_bg");
  const base = bg ? c.slice(0, -3) : c;
  if (!COLORS.has(base)) {
    return null;
  }
  return bg ? `wv-bg-${base}` : `wv-color-${base}`;
}

/** A block's trailing `{key="value" …}` attribute (Notion appends one to a block's first line). */
export interface TrailingAttrs {
  /** The text with the trailing `{…}` removed. */
  rest: string;
  color: string | null;
  toggle: boolean;
}

const TRAILING = /\s*\{([^{}]*)\}\s*$/;

/** Splits a trailing `{color="…" toggle="true"}` off a block's text, returning the stripped text + parsed attrs. */
export function parseTrailingAttrs(text: string): TrailingAttrs {
  const match = TRAILING.exec(text);
  const body = match?.[1] ?? "";
  const color = /color="([^"]*)"/.exec(body)?.[1] ?? null;
  const toggle = /toggle="true"/.test(body);
  // Only a brace block that actually carries a Notion attribute is stripped — a literal trailing `{note}` is text.
  if (match === null || (color === null && !toggle)) {
    return { rest: text, color: null, toggle: false };
  }
  return { rest: text.slice(0, match.index), color, toggle };
}

const ATTR = /([a-zA-Z-]+)="([^"]*)"/g;

/** The `key="value"` attributes of a custom tag's opening text (e.g. `<callout icon="💡" color="blue_bg">`). */
export function parseTagAttrs(tag: string): Record<string, string> {
  const attrs: Record<string, string> = {};
  for (const match of tag.matchAll(ATTR)) {
    const [, key, value] = match;
    if (key !== undefined && value !== undefined) {
      attrs[key] = value;
    }
  }
  return attrs;
}
