// Which registered source, if any, claims a URL — the web side of the open resolver. Mirrors the host's
// NotionSource.Match so a Notion link opens in the native source renderer (SourceView) instead of a web iframe,
// which Notion blanks via X-Frame-Options. Returns the source id, or null for a plain web URL.
export function sourceIdForUrl(url: string): string | null {
  let host: string;
  try {
    host = new URL(url).host.toLowerCase();
  } catch {
    return null;
  }
  if (host === "notion.so" || host.endsWith(".notion.so") || host.endsWith(".notion.site")) {
    return "notion";
  }
  return null;
}
