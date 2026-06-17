import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { type FontWeight, type ILink, Terminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { type JSX, onCleanup, onMount } from "solid-js";
import { type TermSession, log, onHostMessage, postToHost } from "../bridge";
import { currentFonts, onFontsChanged } from "../fonts";
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
export function TerminalView(props: {
  session: TermSession;
  // Called once on mount with a function that moves keyboard focus into this terminal, so the layout
  // can delegate Ctrl+N pane switching to the live xterm (its hidden textarea, not the container div).
  onReady?: (focus: () => void) => void;
}): JSX.Element {
  let container!: HTMLDivElement;

  // Typography is a user setting resolved by the host (global font.* + terminal.font.* overrides),
  // injected before navigation so the terminal mounts at the right font, and live-updated in onMount.
  const initialFont = currentFonts().terminal;
  const term = new Terminal({
    fontFamily: initialFont.family,
    fontSize: initialFont.size,
    fontWeight: initialFont.weight as FontWeight,
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

    // Re-fit to the container and force a repaint. Used for every event that can leave the WebGL
    // canvas stale/blank or the PTY sized to the wrong grid: layout changes, OS-window resizes, a
    // lost GL context, and HMR swaps. fit() updates cols/rows (and so notifies the PTY via onResize);
    // refresh() guarantees a paint even when the size is unchanged (the blank-after-HMR case).
    const refit = (): void => {
      try {
        fit.fit();
        term.refresh(0, term.rows - 1);
      } catch {
        // fit/refresh can throw mid-layout when the pane has zero size; ignore.
      }
    };

    // Apply live font changes: update xterm's options, then refit since the cell metrics (and thus
    // cols/rows, which the PTY must learn) change with the font.
    const offFonts = onFontsChanged((config) => {
      term.options.fontFamily = config.terminal.family;
      term.options.fontSize = config.terminal.size;
      term.options.fontWeight = config.terminal.weight as FontWeight;
      refit();
    });

    // WebGL renderer with self-healing: a lost GL context (driver churn, or DOM/style mutation from
    // an HMR swap in dev) otherwise leaves the canvas blank. On loss, drop the addon so xterm falls
    // back to the always-painting DOM renderer, then repaint.
    try {
      const webgl = new WebglAddon();
      webgl.onContextLoss(() => {
        webgl.dispose();
        refit();
      });
      term.loadAddon(webgl);
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

    refit();

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

    // Hand the layout a way to focus this terminal (Ctrl+N pane switching delegates here).
    props.onReady?.(() => term.focus());

    const resizeObserver = new ResizeObserver(() => refit());
    resizeObserver.observe(container);

    // Backup for OS-window resizes: WebView2 doesn't reliably deliver those to the container's
    // ResizeObserver, so without this the PTY keeps its old cols/rows until the next manual pane
    // resize — i.e. the claude TUI never learns the window changed size.
    window.addEventListener("resize", refit);

    // After an HMR update (e.g. a CSS/theme hot-swap) there's no size change to trigger a refit, yet
    // the WebGL canvas can be left blank — so refit + repaint explicitly. Dev-only: `import.meta.hot`
    // is undefined in the production build, so this is tree-shaken out.
    if (import.meta.hot) {
      import.meta.hot.on("vite:afterUpdate", refit);
    }

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
      offFonts();
      resizeObserver.disconnect();
      window.removeEventListener("resize", refit);
      if (import.meta.hot) {
        import.meta.hot.off("vite:afterUpdate", refit);
      }
      term.dispose();
    });
  });

  return <div class="term" ref={container} />;
}
