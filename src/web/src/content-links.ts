export type ContentLinkKind = "url" | "file" | "ref";

export interface ContentLinkMatch {
  start: number;
  end: number;
  text: string;
  kind: ContentLinkKind;
}

const PATH = String.raw`(?:[A-Za-z]:)?(?:[~.]{0,2}[\\/])?[\w.\\/@-]+\.[A-Za-z0-9]+`;
// `:line`, `:line:col`, and the range form agents print for a hunk (`:46-96`, `:46:2-96:8`) — the whole
// reference underlines as one link, and a click reveals its named first line.
const LINE = String.raw`:(?<line>\d+)(?::\d+)?(?:-\d+(?::\d+)?)?`;
const FILE_LINE = new RegExp(`${PATH}${LINE}`, "g");
const TRAILING_LINE = new RegExp(`^(.*?)${LINE}$`);
const FILE_LINE_EXACT = new RegExp(`^${PATH}${LINE}$`);
const FILE_URI_RE = /file:\/\/\/[^\s"'<>()]*[^\s"'<>().,;:!?]/g;
const URL_RE = /https?:\/\/[^\s"'<>()]*[^\s"'<>().,;:!?]/g;
const TOOL_PATH = new RegExp(`(?<=[A-Za-z]\\()${PATH}(?:${LINE})?(?=\\))`, "g");
const BARE_PATH =
  /(?:[A-Za-z]:)?(?:[~.]{0,2}[\\/][\w.@-]+(?:[\\/][\w.@-]+)*|[\w.@-]+(?:[\\/][\w.@-]+)+)\.[A-Za-z][A-Za-z0-9]*/g;
const REF_RE = /(?<![\w#&])#[1-9]\d*(?!\w)/g;

export function findContentLinks(text: string, includeRefs: boolean): ContentLinkMatch[] {
  const matches: ContentLinkMatch[] = [];
  const claimed: Array<[number, number]> = [];
  collect(text, URL_RE, "url", matches, claimed);
  collect(text, FILE_URI_RE, "file", matches, claimed);
  if (includeRefs) {
    collect(text, REF_RE, "ref", matches, claimed);
  }
  collect(text, FILE_LINE, "file", matches, claimed);
  collect(text, TOOL_PATH, "file", matches, claimed);
  collect(text, BARE_PATH, "file", matches, claimed);
  return matches.sort((a, b) => a.start - b.start);
}

/** True when the value is exactly a `path:line` reference — the shape a URI parser misreads as a scheme. */
export function isFileLineReference(value: string): boolean {
  return FILE_LINE_EXACT.test(value);
}

export function parseFileReference(value: string): { path: string; line: number } {
  if (value.startsWith("file:///")) {
    const url = new URL(value);
    const line = /\d+/.exec(url.hash)?.[0];
    return {
      path: decodeURIComponent(url.pathname),
      line: line === undefined ? 1 : Number(line),
    };
  }
  const match = TRAILING_LINE.exec(value);
  return match === null
    ? { path: value, line: 1 }
    : { path: match[1] ?? "", line: Number(match.groups?.line) };
}

function collect(
  text: string,
  pattern: RegExp,
  kind: ContentLinkKind,
  matches: ContentLinkMatch[],
  claimed: Array<[number, number]>,
): void {
  pattern.lastIndex = 0;
  for (let match = pattern.exec(text); match !== null; match = pattern.exec(text)) {
    const start = match.index;
    const end = start + match[0].length;
    if (claimed.some(([from, to]) => start < to && end > from)) {
      continue;
    }
    claimed.push([start, end]);
    matches.push({ start, end, text: match[0], kind });
  }
}
