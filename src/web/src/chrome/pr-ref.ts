/** Where to open: a PR number, plus owner/repo when it came from a pasted URL (so the host can reject a foreign repo). */
export interface OpenPrTarget {
  number: number;
  owner: string;
  repo: string;
}

// A direct PR reference typed/pasted into the filter: a full URL (.../owner/repo/pull/123), "#123", or a bare
// number. Returns null for a free-text search query. Pure (no DOM), so it's unit-tested in isolation.
export function parsePrRef(input: string): OpenPrTarget | null {
  const text = input.trim();
  const url = text.match(/(?:^|\/)([^/\s]+)\/([^/\s]+)\/pull\/(\d+)\b/);
  if (url) {
    return { number: Number(url[3]), owner: url[1] ?? "", repo: url[2] ?? "" };
  }
  const hash = text.match(/^#(\d+)$/);
  if (hash) {
    return { number: Number(hash[1]), owner: "", repo: "" };
  }
  if (/^\d+$/.test(text)) {
    return { number: Number(text), owner: "", repo: "" };
  }
  return null;
}
