import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { type ILink, Terminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { type JSX, onCleanup, onMount } from "solid-js";
import { type TermSession, log, onHostMessage, postToHost } from "../bridge";
import { base64ToBytes, bytesToBase64 } from "./base64";

// Matches paths with an extension followed by :line (optionally :col), e.g.
// src/foo.ts:42, /abs/bar.cs:10:5, ./baz.py:7
const FILE_LINE = /(?:[~.]{0,2}\/)?[\w./-]+\.[A-Za-z0-9]+:\d+(?::\d+)?/g;

function revealFromMatch(matchText: string): void {
  const [path, lineText] = matchText.split(":");
  if (path !== undefined && path.length > 0) {
    postToHost({ type: "reveal-file", path, line: Number(lineText) || 1 });
  }
}

// xterm.js pane wired to one C# PTY session over the bridge:
//   PTY bytes  -> { term-output } -> term.write(Uint8Array)
//   keystrokes -> term.onData    -> { term-input }
//   layout     -> FitAddon       -> { term-resize }
// On mount it reports { term-ready } so the host launches the session's child (the interactive
// `claude` TUI for "claude", a plain login shell for "shell") sized to match. Every message it
// sends and receives is tagged with `session` so two panes can share the single bridge channel.
export function TerminalView(props: { session: TermSession }): JSX.Element {
  let container!: HTMLDivElement;

  const term = new Terminal({
    fontFamily: 'ui-monospace, "SF Mono", Menlo, monospace',
    fontSize: 13,
    lineHeight: 1.0,
    theme: { background: "#1e1e1e", foreground: "#d4d4d4" },
    cursorBlink: true,
    scrollback: 8000,
    allowProposedApi: true,
  });
  const fit = new FitAddon();
  const encoder = new TextEncoder();

  onMount(() => {
    term.loadAddon(fit);
    term.open(container);
    try {
      term.loadAddon(new WebglAddon());
    } catch (error) {
      log("warn", `WebGL terminal renderer unavailable, using fallback: ${String(error)}`);
    }
    // OSC 8 hyperlinks Claude emits (file:// URIs) -> reveal in Monaco.
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
          }
        } catch {
          // not a parseable URI; ignore
        }
      },
    };

    // Clickable un-hyperlinked file:line references in terminal output.
    term.registerLinkProvider({
      provideLinks(lineNumber, callback) {
        const bufferLine = term.buffer.active.getLine(lineNumber - 1);
        if (bufferLine === undefined) {
          callback(undefined);
          return;
        }
        const text = bufferLine.translateToString(true);
        const links: ILink[] = [];
        FILE_LINE.lastIndex = 0;
        let match = FILE_LINE.exec(text);
        while (match !== null) {
          const startX = match.index + 1;
          const matched = match[0];
          links.push({
            range: {
              start: { x: startX, y: lineNumber },
              end: { x: startX + matched.length, y: lineNumber },
            },
            text: matched,
            activate: () => revealFromMatch(matched),
          });
          match = FILE_LINE.exec(text);
        }
        callback(links.length > 0 ? links : undefined);
      },
    });

    fit.fit();

    term.onData((data) => {
      postToHost({
        type: "term-input",
        session: props.session,
        dataB64: bytesToBase64(encoder.encode(data)),
      });
    });

    term.onResize(({ cols, rows }) => {
      postToHost({ type: "term-resize", session: props.session, cols, rows });
    });

    // Ask the host to start this session's PTY child sized to the fitted terminal.
    postToHost({ type: "term-ready", session: props.session, cols: term.cols, rows: term.rows });

    const resizeObserver = new ResizeObserver(() => {
      try {
        fit.fit();
      } catch {
        // fit can throw mid-layout when the pane has zero size; ignore.
      }
    });
    resizeObserver.observe(container);

    const offHost = onHostMessage((message) => {
      // The bridge channel is shared across sessions; ignore traffic for the other pane.
      if (message.type === "term-output") {
        if (message.session === props.session) {
          term.write(base64ToBytes(message.dataB64));
        }
      } else if (message.type === "term-exit") {
        if (message.session === props.session) {
          term.write(`\r\n\x1b[90m[process exited: ${message.code}]\x1b[0m\r\n`);
        }
      } else if (message.type === "term-reset") {
        if (message.session === props.session) {
          // The host disposed our PTY (e.g. the shell setting changed). Wipe scrollback + state and
          // re-announce readiness so the host relaunches the child sized to the current pane.
          term.reset();
          postToHost({
            type: "term-ready",
            session: props.session,
            cols: term.cols,
            rows: term.rows,
          });
        }
      }
    });

    onCleanup(() => {
      offHost();
      resizeObserver.disconnect();
      term.dispose();
    });
  });

  return <div class="term" ref={container} />;
}
