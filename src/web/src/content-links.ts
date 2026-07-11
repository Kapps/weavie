export type ContentLinkKind = "url" | "file" | "ref";

export interface ContentLinkMatch {
  start: number;
  end: number;
  text: string;
  kind: ContentLinkKind;
}

const PATH = String.raw`(?:[A-Za-z]:)?(?:[~.]{0,2}[\\/])?[\w.\\/-]+\.[A-Za-z0-9]+`;
const FILE_LINE = new RegExp(String.raw`${PATH}:\d+(?::\d+)?`, "g");
const FILE_URI_RE = /file:\/\/\/[^\s"'<>()]*[^\s"'<>().,;:!?]/g;
const URL_RE = /https?:\/\/[^\s"'<>()]*[^\s"'<>().,;:!?]/g;
const TOOL_PATH = new RegExp(String.raw`(?<=[A-Za-z]\()${PATH}(?::\d+(?::\d+)?)?(?=\))`, "g");
const BARE_PATH =
  /(?:[A-Za-z]:)?(?:[~.]{0,2}[\\/][\w.-]+(?:[\\/][\w.-]+)*|[\w.-]+(?:[\\/][\w.-]+)+)\.[A-Za-z][A-Za-z0-9]*/g;
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

export function parseFileReference(value: string): { path: string; line: number } {
  if (value.startsWith("file:///")) {
    const url = new URL(value);
    const line = /\d+/.exec(url.hash)?.[0];
    return {
      path: decodeURIComponent(url.pathname),
      line: line === undefined ? 1 : Number(line),
    };
  }
  const match = /^(.*?):(\d+)(?::\d+)?$/.exec(value);
  return { path: match?.[1] ?? value, line: match === null ? 1 : Number(match[2]) };
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
