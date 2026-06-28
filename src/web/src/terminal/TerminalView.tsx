import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { type FontWeight, Terminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { type JSX, createEffect, onCleanup, onMount } from "solid-js";
import { type TermSession, isBrowserHostedShell, log, onHostMessage, postToHost } from "../bridge";
import { IS_MAC } from "../commands/keybindings";
import { currentFonts, onFontsChanged } from "../fonts";
import { currentXtermTheme, onXtermThemeChanged } from "../theme";
import { base64ToBytes, bytesToBase64 } from "./base64";
import { attachOsc52, noteTerminalFocus, registerTerminal } from "./host-clipboard";
import { wireTerminalLinks } from "./terminal-links";

// Windows file URIs (OSC 7) surface as "/C:/..." — strip the leading slash so it's a real path.
function uriToPath(pathname: string): string {
  const path = decodeURIComponent(pathname);
  return /^\/[A-Za-z]:/.test(path) ? path.slice(1) : path;
}

// xterm.js pane wired to one C# PTY over the bridge: PTY bytes -> term.write, keystrokes -> term-input,
// layout -> term-resize. On mount it reports { term-ready } so the host launches this session's child sized
// to match. Every message is tagged with `slot` (workspace session) AND `session` (pane) so the host routes
// to the right PTY. Each loaded session keeps its pane mounted; only the active one shows (pure show/hide).
export function TerminalView(props: {
  // The workspace session (rail id) and pane this xterm is bound to.
  slot: string;
  pane: TermSession;
  // Whether this is the visible session for its pane. Drives WebGL mount/dispose — one GPU context per
  // visible pane (one per session would blow the WebGL-context cap); a hidden pane keeps its buffer alive.
  active: boolean;
  // Called once on mount with a focus fn, so the layout can delegate Ctrl+N / focus-pane to the live xterm.
  onFocusReady?: (focus: () => void) => void;
  // Called when the child sets the terminal title (OSC 0/2), so the pane header can show it.
  onTitle?: (title: string) => void;
  // Called once when this terminal paints its first frame, so the shell can dismiss the startup splash on the
  // terminal (the primary surface) instead of waiting for the editor.
  onFirstRender?: () => void;
  // Right-click on the terminal body, after this pane has taken focus (so the copy/paste/clear commands target
  // it). The shell opens the shared context menu.
  onContextMenu?: (event: MouseEvent) => void;
}): JSX.Element {
  let container!: HTMLDivElement;

  // Host-resolved font setting injected before navigation so the terminal mounts at the right font; live-updated in onMount.
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
    // Shell pane: advertise enhanced keyboard input so its line editor can negotiate Shift+Enter et al. (e.g.
    // newline-without-submit) like it does under Windows Terminal — win32-input-mode is the Windows/ConPTY path,
    // kitty covers POSIX shells. The claude pane is left legacy: it never negotiates and gets Shift+Enter from
    // the injected handler below, so enabling the protocol there would only mis-encode keys it doesn't expect.
    ...(props.pane === "shell"
      ? { vtExtensions: { win32InputMode: true, kittyKeyboard: true } }
      : {}),
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

    // Set on unmount so the async fonts.ready callback below never touches a disposed terminal.
    let disposed = false;

    // Re-fit to the container (updating cols/rows, notifying the PTY) and force a repaint, for any event
    // that can leave the canvas stale or the PTY mis-sized. Both throw on a zero-size (hidden) pane — ignored.
    const refit = (): void => {
      // Only the visible session's pane drives its PTY size; a background session's pane (hidden) must not
      // fit + emit term-resize, which would resize that session's TUI behind the user's back. It refits on
      // becoming active (the props.active effect below).
      if (!props.active) {
        return;
      }
      try {
        fit.fit();
        term.refresh(0, term.rows - 1);
      } catch {
        // zero-size pane mid-layout; ignore.
      }
    };

    // Apply live font changes, then refit since cell metrics (and thus the PTY's cols/rows) change with the font.
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

    // WebGL renderer with self-healing. On macOS/WKWebView the GPU context can be lost, sometimes WITHOUT
    // firing `webglcontextlost`. A reported loss drops the addon and falls back to the DOM renderer; the
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

    // Keep a WebGL context only for the visible pane (one per hidden session would exceed the context cap).
    // On hide, drop the GPU context but keep the Terminal + scrollback alive so switching back is instant.
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

    // Dismiss the startup splash on the first painted terminal frame (the primary surface), not on editor-ready.
    // Fires once, then detaches.
    const renderSub = term.onRender(() => {
      renderSub.dispose();
      props.onFirstRender?.();
    });

    // OSC 8 + auto-detected file:line and http(s) links (file:// → Monaco, URLs → OS browser).
    wireTerminalLinks(term);

    // Clipboard: register this pane for the copy/paste commands, route Claude's OSC 52 to the OS clipboard,
    // and note focus so the commands act on the terminal the user is in.
    const offRegister = registerTerminal(termKey, term);
    const offClipboard = attachOsc52(term);
    const onContainerFocus = (): void => noteTerminalFocus(termKey);
    container.addEventListener("focusin", onContainerFocus);

    // OSC 0/2 title → the pane header (web-only; no host round-trip).
    term.onTitleChange((title) => props.onTitle?.(title));

    // OSC 7 cwd → the host, so a reopened shell relaunches where the user was.
    const offCwd = term.parser.registerOscHandler(7, (data) => {
      try {
        postToHost({
          type: "term-cwd",
          slot: props.slot,
          session: props.pane,
          cwd: uriToPath(new URL(data).pathname),
        });
      } catch {
        // not a parseable file URI; ignore
      }
      return true;
    });

    refit();

    // The bundled default font can finish loading AFTER term.open() measured cell metrics against the
    // fallback, misaligning text. Once fonts are ready, re-assert fontFamily (forcing a re-measure) and refit.
    void document.fonts.ready.then(() => {
      if (disposed) {
        return;
      }

      term.options.fontFamily = currentFonts().terminal.family;
      refit();
    });

    const sendInput = (data: string): void => {
      postToHost({
        type: "term-input",
        slot: props.slot,
        session: props.pane,
        dataB64: bytesToBase64(encoder.encode(data)),
      });
    };

    // Shift+Enter → newline (not submit): send the standard kitty sequence for it (CSI 13;2u), which claude
    // parses. Claude never enables the protocol (it runs legacy and only parses incoming CSI-u), so we emit just
    // this one chord and leave every other key legacy — force-enabling the whole protocol would also re-encode
    // Ctrl+C etc. as CSI-u, which claude doesn't expect. Claude-pane only, so the shell isn't fed CSI-u.
    term.attachCustomKeyEventHandler((e) => {
      // Ctrl+V / ⌘V on a served browser tab: the paste command declined (the browser blocks
      // navigator.clipboard.readText), so return false to stop xterm eating it as ^V and let the browser's
      // native paste event fire — the one clipboard read a browser allows. The native WebView pastes via the command.
      if (
        isBrowserHostedShell() &&
        e.type === "keydown" &&
        e.key.toLowerCase() === "v" &&
        (IS_MAC ? e.metaKey && !e.ctrlKey : e.ctrlKey && !e.metaKey) &&
        !e.shiftKey &&
        !e.altKey
      ) {
        return false;
      }
      if (
        props.pane === "claude" &&
        e.type === "keydown" &&
        e.key === "Enter" &&
        e.shiftKey &&
        !e.ctrlKey &&
        !e.altKey &&
        !e.metaKey
      ) {
        e.preventDefault();
        sendInput("\x1b[13;2u");
        return false;
      }
      return true;
    });

    term.onData(sendInput);

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

    // Register this pane's focus fn so the layout can land keyboard focus here (Ctrl+N / focus-pane).
    props.onFocusReady?.(() => term.focus());

    const resizeObserver = new ResizeObserver(() => refit());
    resizeObserver.observe(container);

    // Backup for OS-window resizes: WebView2 doesn't reliably deliver those to the ResizeObserver, so without
    // this the PTY keeps its old cols/rows (the claude TUI never learns the window changed size).
    window.addEventListener("resize", refit);

    // An HMR update has no size change to trigger a refit, and WebKit's recompositing pass can silently
    // blank the WebGL canvas (see mountWebgl), so rebuild the addon (or just refit on the DOM renderer). On
    // the NEXT frame — rebuilding synchronously races WebKit's post-update layout and the new canvas blanks
    // too. Dev-only: `import.meta.hot` is undefined in production, so this is tree-shaken out.
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
      // The bridge channel is shared across panes AND sessions; ignore traffic not for this (slot, pane) pair.
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
          // A respawn relaunches the child (which re-establishes every mode), so a full reset is right; a
          // non-respawn reset clears content + scrollback WITHOUT touching terminal modes (mouse tracking).
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
      disposed = true;
      renderSub.dispose();
      offHost();
      offFonts();
      offTheme();
      offRegister();
      offClipboard.dispose();
      offCwd.dispose();
      container.removeEventListener("focusin", onContainerFocus);
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

  return (
    <div
      class="term"
      ref={container}
      onContextMenu={(event) => {
        if (props.onContextMenu === undefined) {
          return;
        }
        event.preventDefault();
        term.focus(); // make this the focused terminal so the menu's copy/paste/clear act on it
        props.onContextMenu(event);
      }}
    />
  );
}
