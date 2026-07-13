import { findContentLinks, parseFileReference } from "../content-links";

export type AgentTextPart =
  | { kind: "text"; text: string }
  | { kind: "url"; text: string; target: string }
  | { kind: "file"; text: string; path: string; line: number }
  | { kind: "ref"; text: string; number: string };

// Renders the shared link grammar (findContentLinks) as parts for the SolidJS plain-text pane, splicing
// unmatched spans back in as text so the original string round-trips.
export function linkAgentText(text: string, allowRefs: boolean): AgentTextPart[] {
  const parts: AgentTextPart[] = [];
  let offset = 0;
  for (const match of findContentLinks(text, allowRefs)) {
    if (match.start > offset) parts.push({ kind: "text", text: text.slice(offset, match.start) });
    if (match.kind === "url") {
      parts.push({ kind: "url", text: match.text, target: match.text });
    } else if (match.kind === "ref") {
      parts.push({ kind: "ref", text: match.text, number: match.text.slice(1) });
    } else {
      const { path, line } = parseFileReference(match.text);
      parts.push({ kind: "file", text: match.text, path, line });
    }
    offset = match.end;
  }
  if (offset < text.length) parts.push({ kind: "text", text: text.slice(offset) });
  return parts;
}
