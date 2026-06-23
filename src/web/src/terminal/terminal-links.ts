// Terminal hyperlinks: OSC 8 links (file:// → reveal in Monaco, http(s) → OS browser) plus auto-detected
// bare file:line references and URLs in the output. The browser-open + file-reveal both round-trip the host.

import type { ILink, Terminal } from "@xterm/xterm";
import { postToHost } from "../bridge";

// A path with an extension followed by :line (optionally :col), e.g. src/foo.ts:42.
const FILE_LINE = /(?:[~.]{0,2}\/)?[\w./-]+\.[A-Za-z0-9]+:\d+(?::\d+)?/g;
// A bare http(s) URL (stops at whitespace and common trailing delimiters).
const URL_RE = /https?:\/\/[^\s"'<>()]+/g;

function revealFile(matchText: string): void {
  const [path, lineText] = matchText.split(":");
  if (path !== undefined && path.length > 0) {
    postToHost({ type: "reveal-file", path, line: Number(lineText) || 1 });
  }
}

function openUrl(url: string): void {
  postToHost({ type: "open-url", url });
}

// Pushes an xterm ILink for each regex match on one buffer line. `skip` excludes matches that fall inside an
// already-claimed span (so a URL ending in .ext:line isn't also linked as a file:line).
function collect(
  text: string,
  pattern: RegExp,
  lineNumber: number,
  activate: (matchText: string) => void,
  links: ILink[],
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
    const matched = match[0];
    links.push({
      range: {
        start: { x: start + 1, y: lineNumber },
        end: { x: start + 1 + matched.length, y: lineNumber },
      },
      text: matched,
      activate: () => activate(matched),
    });
  }
}

/** Wires OSC 8 link activation + the auto-link provider (URLs first, so URL-embedded file:line isn't double-linked). */
export function wireTerminalLinks(term: Terminal): void {
  term.options.linkHandler = {
    activate: (_event, uri) => {
      try {
        const url = new URL(uri);
        if (url.protocol === "file:") {
          const lineMatch = /(\d+)/.exec(url.hash);
          postToHost({
            type: "reveal-file",
            path: decodeURIComponent(url.pathname),
            line: lineMatch ? Number(lineMatch[1]) : 1,
          });
        } else if (url.protocol === "http:" || url.protocol === "https:") {
          openUrl(uri);
        }
      } catch {
        // not a parseable URI; ignore
      }
    },
  };

  term.registerLinkProvider({
    provideLinks(lineNumber, callback) {
      const bufferLine = term.buffer.active.getLine(lineNumber - 1);
      if (bufferLine === undefined) {
        callback(undefined);
        return;
      }
      const text = bufferLine.translateToString(true);
      const links: ILink[] = [];
      const claimed: Array<[number, number]> = [];
      collect(text, URL_RE, lineNumber, openUrl, links, claimed);
      collect(text, FILE_LINE, lineNumber, revealFile, links, claimed);
      callback(links.length > 0 ? links : undefined);
    },
  });
}
