import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { Terminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { type JSX, onCleanup, onMount } from "solid-js";
import { log, onHostMessage, postToHost } from "../bridge";
import { base64ToBytes, bytesToBase64 } from "./base64";

// xterm.js pane wired to the C# PTY over the bridge:
//   PTY bytes  -> { term-output } -> term.write(Uint8Array)
//   keystrokes -> term.onData    -> { term-input }
//   layout     -> FitAddon       -> { term-resize }
// On mount it reports { term-ready } so the host launches `claude` sized to match.
export function TerminalView(): JSX.Element {
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
    fit.fit();

    term.onData((data) => {
      postToHost({ type: "term-input", dataB64: bytesToBase64(encoder.encode(data)) });
    });

    term.onResize(({ cols, rows }) => {
      postToHost({ type: "term-resize", cols, rows });
    });

    // Ask the host to start the PTY child sized to the fitted terminal.
    postToHost({ type: "term-ready", cols: term.cols, rows: term.rows });

    const resizeObserver = new ResizeObserver(() => {
      try {
        fit.fit();
      } catch {
        // fit can throw mid-layout when the pane has zero size; ignore.
      }
    });
    resizeObserver.observe(container);

    const offHost = onHostMessage((message) => {
      if (message.type === "term-output") {
        term.write(base64ToBytes(message.dataB64));
      } else if (message.type === "term-exit") {
        term.write(`\r\n\x1b[90m[process exited: ${message.code}]\x1b[0m\r\n`);
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
