import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { type FontWeight, type ILink, Terminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { type JSX, createEffect, onCleanup, onMount } from "solid-js";
import { type TermSession, log, onHostMessage, postToHost } from "../bridge";
import { currentFonts, onFontsChanged } from "../fonts";
import { currentXtermTheme, onXtermThemeChanged } from "../theme";
import { base64ToBytes, bytesToBase64 } from "./base64";

// Matches a path with an extension followed by :line (optionally :col), e.g. src/foo.ts:42.
const FILE_LINE = /(?:[~.]{0,2}\/)?[\w./-]+\.[A-Za-z0-9]+:\d+(?::\d+)?/g;

function revealFromMatch(matchText: string): void {
  const [path, lineText] = matchText.split(":");
  if (path !== undefined && path.length > 0) {
    postToHost({ type: "reveal-file", path, line: Number(lineText) || 1 });
  }
}

// xterm.js pane wired to one C# PTY over the bridge: PTY bytes -> term.write, keystrokes -> term-input,
// layout -> term-resize. On mount it reports { term-ready } so the host launches (or repaints) this
// session's child sized to match. Every message is tagged with `slot` (the workspace session) AND
// `session` (the pane within it) so the host routes to the right PTY and output reaches the right xterm.
// Each loaded session keeps its own pane mounted; only the active one is shown, so switching sessions is
// pure show/hide (see the `active` effect below).
export function TerminalView(props: {
  // The workspace session (rail id) and pane this xterm is bound to.
  slot: string;
  pane: TermSession;
  // Whether this is the visible session for its pane. Drives WebGL mount/dispose — one GPU context per
  // visible pane, since one per session would blow the browser's WebGL-context cap. A hidden pane keeps
  // its Terminal + buffer alive, so switching back is instant.
  active: boolean;
  // Called once on mount with a function that focuses this terminal's textarea, so the layout can
  // delegate Ctrl+N / focus-pane to the active session's live xterm.
  onFocusReady?: (focus: () => void) => void;
}): JSX.Element {
  let container!: HTMLDivElement;

  // Typography is a host-resolved user setting injected before navigation, so the terminal mounts at the
  // right font; live-updated in onMount.
  const initialFont = currentFonts().terminal;
  const term = new Terminal({
    fontFamily: initialFont.family,
    fontSize: initialFont.size,
    fontWeight: initialFont.weight as FontWeight,
    lineHeight: 1.0,
    theme: currentXtermTheme(),
    cursorBlink: true,
    scrollback: 8000,
    allowProposedApi: true,
  });
  const fit = new FitAddon();
  const encoder = new TextEncoder();
  // Introspection key (e2e/diagnostics): slot + pane, so two sessions' panes don't collide.
  const termKey = `${props.slot}:${props.pane}`;

  onMount(() => {
    term.loadAddon(fit);
    term.open(container);

    // Publish this pane's terminal for e2e / diagnostics introspection (read-only). See global.d.ts.
    window.__WEAVIE_TERMINALS__ ??= {};
    window.__WEAVIE_TERMINALS__[termKey] = term;

    // Re-fit to the container and force a repaint, for any event that can leave the WebGL canvas
    // stale/blank or the PTY sized to the wrong grid (layout/window resize, lost GL context, becoming
    // visible, HMR). fit() updates cols/rows (notifying the PTY); refresh() guarantees a paint even at an
    // unchanged size. Both throw when the pane has zero size (a hidden session) — caught and ignored.
    const refit = (): void => {
      try {
        fit.fit();
        term.refresh(0, term.rows - 1);
      } catch {
        // zero-size pane mid-layout; ignore.
      }
    };

    // Apply live font changes, then refit since cell metrics (and thus cols/rows the PTY must learn)
    // change with the font.
    const offFonts = onFontsChanged((config) => {
      term.options.fontFamily = config.terminal.family;
      term.options.fontSize = config.terminal.size;
      term.options.fontWeight = config.terminal.weight as FontWeight;
      refit();
    });

    // Apply live theme changes to this terminal.
    const offTheme = onXtermThemeChanged((theme) => {
      term.options.theme = theme;
    });

    // WebGL renderer with self-healing. On macOS/WKWebView the GPU context can be lost (driver churn, or
    // WebKit's recompositing pass after an HMR DOM swap), often WITHOUT firing `webglcontextlost`, so a
    // same-size refresh() can't recover it — only an actual size change reallocates the backing store.
    // When the loss IS reported we drop the addon and fall back to the always-painting DOM renderer; the
    // silent case is handled by rebuilding on HMR (see below).
    let webgl: WebglAddon | null = null;
    const mountWebgl = (): void => {
      try {
        const addon = new WebglAddon();
        addon.onContextLoss(() => {
          if (webgl === addon) {
            webgl = null;
          }
          addon.dispose();
          refit();
        });
        term.loadAddon(addon);
        webgl = addon;
      } catch (error) {
        webgl = null;
        log("warn", `WebGL terminal renderer unavailable, using fallback: ${String(error)}`);
      }
    };

    // Keep a WebGL context only for the visible pane, since one per hidden session would exceed the
    // browser's context cap. On show: (re)mount WebGL and refit to the laid-out container next frame. On
    // hide: drop the GPU context but keep the Terminal + scrollback alive, so switching back is instant.
    createEffect(() => {
      if (props.active) {
        if (webgl === null) {
          mountWebgl();
        }
        requestAnimationFrame(() => refit());
      } else if (webgl !== null) {
        webgl.dispose();
        webgl = null;
      }
    });

    // OSC 8 file:// hyperlinks Claude emits -> reveal in Monaco.
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
        slot: props.slot,
        session: props.pane,
        dataB64: bytesToBase64(encoder.encode(data)),
      });
    });

    term.onResize(({ cols, rows }) => {
      postToHost({ type: "term-resize", slot: props.slot, session: props.pane, cols, rows });
    });

    // Ask the host to start (or repaint) this session's PTY child sized to the fitted terminal.
    postToHost({
      type: "term-ready",
      slot: props.slot,
      session: props.pane,
      cols: term.cols,
      rows: term.rows,
    });

    // Register this pane's focus fn so the layout can land keyboard focus here (Ctrl+N / focus-pane); it
    // resolves which loaded session's pane to focus by the active session.
    props.onFocusReady?.(() => term.focus());

    const resizeObserver = new ResizeObserver(() => refit());
    resizeObserver.observe(container);

    // Backup for OS-window resizes: WebView2 doesn't reliably deliver those to the container's
    // ResizeObserver, so without this the PTY keeps its old cols/rows until the next manual pane
    // resize — i.e. the claude TUI never learns the window changed size.
    window.addEventListener("resize", refit);

    // After an HMR update there's no size change to trigger a refit, and WebKit's recompositing pass can
    // silently blank the WebGL canvas (see mountWebgl). So on the WebGL renderer, rebuild the addon so
    // WebKit allocates and composites a fresh canvas + GL context cleanly; on the DOM renderer a plain
    // refit repaints. Do it on the NEXT frame, after WebKit's own post-update layout — rebuilding
    // synchronously races that pass and the replacement canvas can blank too. Dev-only: `import.meta.hot`
    // is undefined in the production build, so this is tree-shaken out.
    const onHmrUpdate = (): void => {
      requestAnimationFrame(() => {
        if (webgl !== null) {
          webgl.dispose();
          webgl = null;
          mountWebgl();
        }
        refit();
      });
    };
    if (import.meta.hot) {
      import.meta.hot.on("vite:afterUpdate", onHmrUpdate);
    }

    const offHost = onHostMessage((message) => {
      // The bridge channel is shared across panes AND sessions; ignore traffic that isn't this exact
      // (slot, pane) pair so each session's output only reaches its own xterm.
      if (message.type === "term-output") {
        if (message.slot === props.slot && message.session === props.pane) {
          term.write(base64ToBytes(message.dataB64));
        }
      } else if (message.type === "term-exit") {
        if (message.slot === props.slot && message.session === props.pane) {
          term.write(`\r\n\x1b[90m[process exited: ${message.code}]\x1b[0m\r\n`);
        }
      } else if (message.type === "term-reset") {
        if (message.slot === props.slot && message.session === props.pane) {
          // A respawn is a deliberate child relaunch (shell setting changed): the fresh child
          // re-establishes every mode, so a full reset is right. A non-respawn reset clears content +
          // scrollback WITHOUT touching terminal modes (mouse tracking).
          if (message.respawn) {
            term.reset();
          } else {
            term.write("\x1b[H\x1b[2J\x1b[3J");
          }
          postToHost({
            type: "term-ready",
            slot: props.slot,
            session: props.pane,
            cols: term.cols,
            rows: term.rows,
          });
        }
      }
    });

    onCleanup(() => {
      offHost();
      offFonts();
      offTheme();
      resizeObserver.disconnect();
      window.removeEventListener("resize", refit);
      if (import.meta.hot) {
        import.meta.hot.off("vite:afterUpdate", onHmrUpdate);
      }
      if (window.__WEAVIE_TERMINALS__?.[termKey] === term) {
        delete window.__WEAVIE_TERMINALS__[termKey];
      }
      webgl?.dispose();
      term.dispose();
    });
  });

  return <div class="term" ref={container} />;
}
