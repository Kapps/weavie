import { type SourceDescriptor, sourceRegistry } from "./source-registry";

// Which registered source, if any, claims a URL — the web side of the open resolver. Driven by the host-pushed
// source registry (each source's hosts declared once in Core), so a Notion link opens in the native SourceView
// instead of a web iframe (which Notion blanks via X-Frame-Options), and a new source needs no change here. A
// pattern is an exact host or a `*.` subdomain wildcard. Returns the source id, or null for a plain web URL.
export function matchSource(url: string, sources: SourceDescriptor[]): string | null {
  let host: string;
  try {
    host = new URL(url).host.toLowerCase();
  } catch {
    return null;
  }
  for (const source of sources) {
    if (source.hosts.some((pattern) => hostMatches(host, pattern.toLowerCase()))) {
      return source.id;
    }
  }
  return null;
}

function hostMatches(host: string, pattern: string): boolean {
  return pattern.startsWith("*.") ? host.endsWith(pattern.slice(1)) : host === pattern;
}

/** The source id claiming `url` per the live registry, or null. Used by the open resolver (Open URL, in-doc links). */
export function sourceIdForUrl(url: string): string | null {
  return matchSource(url, sourceRegistry());
}
