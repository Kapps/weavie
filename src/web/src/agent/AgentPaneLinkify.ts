export type AgentTextPart =
  | { kind: "text"; text: string }
  | { kind: "url"; text: string; target: string }
  | { kind: "file"; text: string; path: string; line: number }
  | { kind: "ref"; text: string; number: string };

// Keep this in step with the Claude terminal linker: web/file URIs, path:line, bare paths, and forge refs.
// URL alternatives come first so a path-looking suffix inside a URL is never linked a second time.
const LINK_RE =
  /https?:\/\/[^\s"'<>()]*[^\s"'<>().,;:!?]|file:\/\/\/[^\s"'<>()]*[^\s"'<>().,;:!?]|(?:[A-Za-z]:)?(?:[~.]{0,2}[\\/])?[\w.\\/-]+\.[A-Za-z0-9]+:\d+(?::\d+)?|(?:[A-Za-z]:)?(?:[~.]{0,2}[\\/][\w.-]+(?:[\\/][\w.-]+)*|[\w.-]+(?:[\\/][\w.-]+)+)\.[A-Za-z][A-Za-z0-9]*|(?<![\w#&])#[1-9]\d*(?!\w)/g;

export function linkAgentText(text: string, allowRefs: boolean): AgentTextPart[] {
  const parts: AgentTextPart[] = [];
  let offset = 0;
  LINK_RE.lastIndex = 0;
  for (let match = LINK_RE.exec(text); match !== null; match = LINK_RE.exec(text)) {
    if (match.index > offset) parts.push({ kind: "text", text: text.slice(offset, match.index) });
    const value = match[0];
    if (value.startsWith("http://") || value.startsWith("https://")) {
      parts.push({ kind: "url", text: value, target: value });
    } else if (value.startsWith("file:///")) {
      const url = new URL(value);
      const line = /\d+/.exec(url.hash)?.[0];
      parts.push({
        kind: "file",
        text: value,
        path: decodeURIComponent(url.pathname),
        line: line === undefined ? 1 : Number(line),
      });
    } else if (value.startsWith("#")) {
      parts.push(
        allowRefs
          ? { kind: "ref", text: value, number: value.slice(1) }
          : { kind: "text", text: value },
      );
    } else {
      const location = /^(.*?):(\d+)(?::\d+)?$/.exec(value);
      parts.push({
        kind: "file",
        text: value,
        path: location?.[1] ?? value,
        line: location === null ? 1 : Number(location[2]),
      });
    }
    offset = match.index + value.length;
  }
  if (offset < text.length) parts.push({ kind: "text", text: text.slice(offset) });
  return parts;
}
