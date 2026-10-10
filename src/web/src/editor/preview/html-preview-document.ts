/// `html` with a `<base>` pointing relative URLs at `base`, placed after any doctype so standards mode is kept.
export function withBase(html: string, base: string): string {
  const tag = `<base href="${base.replace(/&/g, "&amp;").replace(/"/g, "&quot;")}">`;
  return html.replace(/^(\s*<!doctype[^>]*>)?/i, (doctype) => doctype + tag);
}
