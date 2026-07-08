// Terminal hyperlinks: OSC 8 links (file:// → reveal in Monaco, http(s) → OS browser) plus auto-detected
// file references (path:line, tool-wrapped, or a bare path) and URLs in the output. The browser-open +
// file-reveal both round-trip the host.

import type { ILink, Terminal } from "@xterm/xterm";
import { isBrowserHostedShell, postToHost, postToLocalHost, type TermSession } from "../bridge";
import { refLinkPrefix } from "./ref-link-store";

// A path with an extension, e.g. src/foo.ts or C:\src\foo.ts. An optional Windows drive prefix (C:\…)
// is matched explicitly so its colon isn't mistaken for a :line suffix.
const PATH = String.raw`(?:[A-Za-z]:)?(?:[~.]{0,2}[\\/])?[\w.\\/-]+\.[A-Za-z0-9]+`;
// A path followed by :line (optionally :col), e.g. src/foo.ts:42:3.
const FILE_LINE = new RegExp(String.raw`${PATH}:\d+(?::\d+)?`, "g");
// A bare http(s) URL (stops at whitespace and common delimiters; the final char must not be sentence
// punctuation, so "see https://host/pr/186." links without the trailing dot).
const URL_RE = /https?:\/\/[^\s"'<>()]*[^\s"'<>().,;:!?]/g;
// A path (:line optional) in a tool-call wrapper, e.g. Edit(src/foo.ts) — the form Claude Code prints
// for file tools, where the parens anchor a path that has no :line.
const TOOL_PATH = new RegExp(String.raw`(?<=[A-Za-z]\()${PATH}(?::\d+(?::\d+)?)?(?=\))`, "g");
// A bare path with no :line and no wrapper, e.g. a src/web/e2e/.recordings/clip.webm reference. Requires a
// path separator AND a letter-initial extension so prose isn't linked: "Node.js", "index.ts", "HTTP/1.1"
// and "16/9.0" don't match — only a real relative/rooted path (a/b.ext, ./x.md, /home/u/a.ts, ~/n.log) does.
const BARE_PATH =
  /(?:[A-Za-z]:)?(?:[~.]{0,2}[\\/][\w.-]+(?:[\\/][\w.-]+)*|[\w.-]+(?:[\\/][\w.-]+)+)\.[A-Za-z][A-Za-z0-9]*/g;
// A bare issue/PR reference like #123 (Claude prints these) → the origin repo's forge page. The lookbehind
// excludes an embedded/entity form (abc#1, ##, &#123;); no leading zero and no trailing word char reject
// #0/#012 and #123abc. Only linked when the repo resolves to a forge (refLinkPrefix != null); "#fff" never
// matches (\d only).
const REF_RE = /(?<![\w#&])#[1-9]\d*(?!\w)/g;

// The originating pane, stamped on every reveal-file so the host resolves a relative path against that shell's
// live OSC 7 cwd (see wireTerminalLinks). `pane` is the TermSession ("shell"/"claude").
type PaneContext = { slot: string; pane: TermSession };

/** Opens a URL in the OS/default browser (the terminal's left-click + the "Open in Browser" menu). */
export function openUrlExternal(url: string): void {
  // The browser lives on the user's machine: a served tab opens the URL itself under the click's user gesture;
  // a native shell asks the LOCAL host, which allowlists http(s) at that trust boundary — untrusted terminal
  // content must never reach a file:// / custom-scheme OS opener. Never a remote backend.
  if (isBrowserHostedShell()) {
    window.open(url, "_blank", "noopener");
    return;
  }
  postToLocalHost({ type: "open-url", url });
}

// Open a terminal `#N` as its forge issue/PR page: the host-pushed prefix for the active session's origin +
// the number. A no-op if the repo isn't a forge (prefix null) — the same gate that keeps the link from forming.
function openRef(matchText: string): void {
  const prefix = refLinkPrefix();
  if (prefix !== null) {
    openUrlExternal(prefix + matchText.slice(1));
  }
}

// One regex hit within a logical (possibly wrapped) line: its flat char span in that line, whether it's a URL
// (URLs get the hover-tracked "Open in…" right-click menu), and how to open it.
type LinkMatch = {
  start: number;
  end: number;
  text: string;
  url: boolean;
  activate: (matchText: string) => void;
};

// Records each regex match on the logical-line text. `claimed` excludes matches that fall inside an already-
// claimed span (so a URL ending in .ext:line isn't also linked as a file:line).
function collect(
  text: string,
  pattern: RegExp,
  url: boolean,
  activate: (matchText: string) => void,
  matches: LinkMatch[],
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
    matches.push({ start, end, text: match[0], url, activate });
  }
}

/**
 * Wires OSC 8 link activation + the auto-link provider (URLs first, so URL-embedded file:line isn't
 * double-linked). Returns a getter for the URL currently under the pointer, so a right-click can offer to
 * open it (browser vs Weavie) instead of activating it — xterm activates links on mouseup for ANY button, so
 * the activate handlers below open only on the primary button and a right-click falls through to the menu.
 */
export function wireTerminalLinks(term: Terminal, context: PaneContext): () => string | undefined {
  let hoveredUrl: string | undefined;

  const revealFile = (matchText: string): void => {
    // Split the trailing :line (or :line:col) from the RIGHT, so a Windows drive colon (C:\…) stays in the path.
    const match = /^(.*?):(\d+)(?::\d+)?$/.exec(matchText);
    const path = match?.[1] ?? matchText;
    if (path.length > 0) {
      postToHost({
        type: "reveal-file",
        path,
        line: match ? Number(match[2]) : 1,
        slot: context.slot,
        session: context.pane,
      });
    }
  };
  // Only track web URLs, so a right-click on a file:// OSC link shows the plain terminal menu, not "open in…".
  const isHttp = (uri: string): boolean => uri.startsWith("http:") || uri.startsWith("https:");

  term.options.linkHandler = {
    activate: (event, uri) => {
      if (event.button !== 0) {
        return;
      }
      try {
        const url = new URL(uri);
        if (url.protocol === "file:") {
          const lineMatch = /(\d+)/.exec(url.hash);
          postToHost({
            type: "reveal-file",
            path: decodeURIComponent(url.pathname),
            line: lineMatch ? Number(lineMatch[1]) : 1,
            slot: context.slot,
            session: context.pane,
          });
        } else if (url.protocol === "http:" || url.protocol === "https:") {
          openUrlExternal(uri);
        }
      } catch {
        // not a parseable URI; ignore
      }
    },
    hover: (_event, uri) => {
      if (isHttp(uri)) {
        hoveredUrl = uri;
      }
    },
    leave: (_event, uri) => {
      if (hoveredUrl === uri) {
        hoveredUrl = undefined;
      }
    },
  };

  term.registerLinkProvider({
    provideLinks(lineNumber, callback) {
      const buffer = term.buffer.active;
      if (buffer.getLine(lineNumber - 1) === undefined) {
        callback(undefined);
        return;
      }
      // Match the whole soft-wrapped logical line, not just this row: xterm flags each continuation row
      // isWrapped, and only those are stitched — a real newline (isWrapped=false) is never joined.
      let startIdx = lineNumber - 1;
      while (startIdx > 0 && buffer.getLine(startIdx)?.isWrapped) {
        startIdx--;
      }
      // Concatenate every row of the logical line, noting where the queried row's own text sits within it.
      let text = "";
      let queryStart = -1;
      let queryEnd = -1;
      for (let idx = startIdx; ; idx++) {
        const line = buffer.getLine(idx);
        if (line === undefined) {
          break;
        }
        const rowStart = text.length;
        text += line.translateToString(false);
        if (idx === lineNumber - 1) {
          queryStart = rowStart;
          queryEnd = text.length;
        }
        if (!buffer.getLine(idx + 1)?.isWrapped) {
          break;
        }
      }
      const matches: LinkMatch[] = [];
      const claimed: Array<[number, number]> = [];
      collect(text, URL_RE, true, openUrlExternal, matches, claimed);
      // After URLs (so a URL's own `#anchor` is already claimed), and only when the origin resolves to a forge.
      if (refLinkPrefix() !== null) {
        collect(text, REF_RE, false, openRef, matches, claimed);
      }
      collect(text, FILE_LINE, false, revealFile, matches, claimed);
      collect(text, TOOL_PATH, false, revealFile, matches, claimed);
      // Last, so a path already claimed as file:line, a tool path, or inside a URL isn't re-linked bare.
      collect(text, BARE_PATH, false, revealFile, matches, claimed);
      // Emit only the slice of each match that lands on the queried row (a single-row range), but open the
      // whole matched target — so hovering or clicking any wrapped fragment reveals the complete path/URL.
      const links: ILink[] = [];
      for (const match of matches) {
        const from = Math.max(match.start, queryStart);
        const to = Math.min(match.end, queryEnd);
        if (from >= to) {
          continue;
        }
        const link: ILink = {
          range: {
            start: { x: from - queryStart + 1, y: lineNumber },
            end: { x: to - queryStart + 1, y: lineNumber },
          },
          text: match.text,
          activate: (event) => {
            if (event.button === 0) {
              match.activate(match.text);
            }
          },
        };
        if (match.url) {
          link.hover = (): void => {
            hoveredUrl = match.text;
          };
          link.leave = (): void => {
            if (hoveredUrl === match.text) {
              hoveredUrl = undefined;
            }
          };
        }
        links.push(link);
      }
      callback(links.length > 0 ? links : undefined);
    },
  });

  return () => hoveredUrl;
}
