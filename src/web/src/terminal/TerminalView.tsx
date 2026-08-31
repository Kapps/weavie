import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { type FontWeight, Terminal } from "@xterm/xterm";
import "@xterm/xterm/css/xterm.css";
import { createEffect, type JSX, onCleanup, onMount } from "solid-js";
import { type ClientSession, isBrowserHostedShell, log, type TermSession } from "../bridge";
import { IS_MAC } from "../commands/keybindings";
import { noteSelectionChange, registerSelectionSource } from "../commands/selection";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { currentFonts, onFontsChanged } from "../fonts";
import { currentXtermTheme, onXtermThemeChanged } from "../theme";
import { base64ToBytes, bytesToBase64 } from "./base64";
import { attachOsc52, noteTerminalFocus, registerTerminal } from "./host-clipboard";
import { attachImagePaste } from "./paste-image";
import { isReplayedQueryAnswer } from "./replay-answers";
import { wireTerminalLinks } from "./terminal-links";
import {
  bindTerminalTouch,
  createTerminalTouchController,
  dispatchTerminalMouseTap,
} from "./terminal-touch";

// Windows file URIs (OSC 7) surface as "/C:/..." — strip the leading slash so it's a real path.
function uriToPath(pathname: string): string {
  const path = decodeURIComponent(pathname);
  return /^\/[A-Za-z]:/.test(path) ? path.slice(1) : path;
}

// How long a hidden pane holds its WebGL context before releasing it. Long enough that a rapid switch
// away-and-back (a PR-switch storm) reuses the live context instead of churning a fresh one each toggle —
// browsers reclaim WebGL contexts lazily, so churn would pile up unfreed contexts and blow the cap.
const HIDDEN_WEBGL_DISPOSE_MS = 2000;
// Monaco's own wheel-scroll animation length, so both panes glide at the same rate under one setting.
const SMOOTH_SCROLL_MS = 125;
const smoothScroll = (): number => (currentEditorOptions().smoothScrolling ? SMOOTH_SCROLL_MS : 0);

// xterm.js pane wired to one C# PTY through its ClientSession feature. The captured feature owns both the
// session and pane identity; on mount `ready` starts/sizes that child. Hidden sessions retain their buffers.
export function TerminalView(props: {
  // The exact live owner and pane this xterm is bound to.
  session: ClientSession;
  pane: TermSession;
  // Stable identity within the pane. Agent terminals use "claude"; every shell tab uses its host-owned id.
  terminalId: string;
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
  // it). `url` is the URL under the pointer (if any), so the menu can offer to open it. The shell opens the menu.
  onContextMenu?: (event: MouseEvent, url: string | undefined) => void;
}): JSX.Element {
  // A pane belongs to the backend that created it. Capture that identity at mount so a cross-backend switch
  // cannot retarget late resize/ready/input callbacks to the newly active host while this pane unmounts.
  const session = props.session;
  const messages = session.feature(
    props.pane === "shell" ? `terminal.shell.${props.terminalId}` : "terminal.agent",
  );
  let container!: HTMLDivElement;
  // Reports the URL currently under the pointer (set once links are wired in onMount), for the right-click menu.
  let hoveredUrl: () => string | undefined = () => undefined;

  // Host-resolved font setting injected before navigation so the terminal mounts at the right font; live-updated in onMount.
  const initialFont = currentFonts().terminal;
  const nativeTouchPaste =
    isBrowserHostedShell() && window.matchMedia("(hover: none) and (pointer: coarse)").matches;
  const term = new Terminal({
    fontFamily: initialFont.family,
    fontSize: initialFont.size,
    fontWeight: initialFont.weight as FontWeight,
    lineHeight: 1.0,
    theme: currentXtermTheme(),
    cursorBlink: true,
    // Without a duration, a wheel notch lands in a single frame; xterm and Monaco share the same scroller.
    smoothScrollDuration: smoothScroll(),
    scrollback: 8000,
    allowProposedApi: true,
    // xterm's own right-click handler unconditionally loads the clicked word into this same hidden textarea
    // (for desktop copy-then-paste-over-selection) — defaulted on for any Mac-reporting `navigator.platform`,
    // which real iPad Safari (the primary native-touch-paste device) also reports. On a real PTY pane, the
    // enlarged touch-sized textarea's tap point can land on live prompt text, so that handler clobbers the
    // value right after onNativePasteInput below clears it. Irrelevant here anyway: native touch paste never
    // shows xterm's own menu (see the `terminal-native-touch-paste` contextmenu pass-through further down).
    rightClickSelectsWord: !nativeTouchPaste,
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
  // Introspection key (e2e/diagnostics): session + exact terminal, with the pane kept queryable.
  const termKey = `${session.connection.id}:${session.address.incarnation}:${props.terminalId}:${props.pane}`;

  onMount(() => {
    term.loadAddon(fit);
    term.open(container);
    const screen = container.querySelector<HTMLElement>(".xterm-screen");
    const textarea = container.querySelector<HTMLTextAreaElement>(".xterm-helper-textarea");
    if (screen === null || textarea === null) {
      throw new Error("Xterm did not mount its input surface");
    }
    const keepNativeTouchTarget = (): void => {
      if (nativeTouchPaste) {
        textarea.style.zIndex = "10";
      }
    };
    keepNativeTouchTarget();
    const nativeCursorSub = nativeTouchPaste ? term.onCursorMove(keepNativeTouchTarget) : undefined;
    const disposeTouch = bindTerminalTouch(
      screen,
      createTerminalTouchController({
        click: dispatchTerminalMouseTap,
        focus: () => term.focus(),
        mouseTrackingMode: () => term.modes.mouseTrackingMode,
      }),
    );

    // Publish this pane's terminal for e2e / diagnostics introspection (read-only). See global.d.ts.
    window.__WEAVIE_TERMINALS__ ??= {};
    window.__WEAVIE_TERMINALS__[termKey] = term;

    // Set on unmount so the async fonts.ready callback below never touches a disposed terminal.
    let disposed = false;

    // Re-fit to the container (updating cols/rows, notifying the PTY) and force a repaint, for any event
    // that can leave the canvas stale or the PTY mis-sized. Both throw on a zero-size (hidden) pane — ignored.
    const refit = (): void => {
      // Only the visible session's pane drives its PTY size; a background session's pane (hidden) must not
      // fit + emit terminal.resize, which would resize that session's TUI behind the user's back. It refits on
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
    const offEditorOptions = onEditorOptionsChanged(() => {
      term.options.smoothScrollDuration = smoothScroll();
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
    // Dropping it is DEFERRED (see HIDDEN_WEBGL_DISPOSE_MS) so a brief hide reuses the live context; the
    // Terminal + scrollback stay alive either way, so switching back is instant.
    let disposeWebglTimer: ReturnType<typeof setTimeout> | undefined;
    const cancelWebglDispose = (): void => {
      if (disposeWebglTimer !== undefined) {
        clearTimeout(disposeWebglTimer);
        disposeWebglTimer = undefined;
      }
    };
    createEffect(() => {
      if (props.active) {
        cancelWebglDispose();
        if (webgl === null) {
          mountWebgl();
        }
        requestAnimationFrame(() => refit());
      } else if (webgl !== null && disposeWebglTimer === undefined) {
        disposeWebglTimer = setTimeout(() => {
          disposeWebglTimer = undefined;
          webgl?.dispose();
          webgl = null;
        }, HIDDEN_WEBGL_DISPOSE_MS);
      }
    });

    // Dismiss the startup splash on the first painted terminal frame (the primary surface), not on editor-ready.
    // Fires once, then detaches.
    const renderSub = term.onRender(() => {
      renderSub.dispose();
      props.onFirstRender?.();
    });

    // OSC 8 + auto-detected file:line and http(s) links (file:// → Monaco, URLs → OS browser).
    hoveredUrl = wireTerminalLinks(term, session);

    // Clipboard: register this pane for the copy/paste commands, route Claude's OSC 52 to the OS clipboard,
    // and note focus so the commands act on the terminal the user is in.
    const offRegister = registerTerminal(termKey, term, session, props.pane);
    const offClipboard = attachOsc52(term);
    // This pane's selection as a search-seed source, read from xterm itself — the copy it mirrors into its
    // hidden helper textarea is an implementation detail, not something to search from.
    const offSelectionSource = registerSelectionSource(termKey, () => term.getSelection());
    const selectionSub = term.onSelectionChange(() => noteSelectionChange(termKey));
    // Image paste (claude pane only): capture an image from the browser paste event → host scratch file → path
    // injected into claude. The shell has no use for it; a pasted path there would just try to run.
    const offImagePaste =
      props.pane === "claude" ? attachImagePaste(container, session) : (): void => {};
    const onContainerFocus = (): void => noteTerminalFocus(termKey);
    container.addEventListener("focusin", onContainerFocus);

    // OSC 0/2 title → the pane header (web-only; no host round-trip).
    term.onTitleChange((title) => props.onTitle?.(title));

    // OSC 7 cwd → the host, so a reopened shell relaunches where the user was.
    const offCwd = term.parser.registerOscHandler(7, (data) => {
      try {
        messages.publish("cwd", {
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

    const sendInput = (data: string, userInitiated: boolean): void => {
      messages.publish("input", {
        dataB64: bytesToBase64(encoder.encode(data)),
        userInitiated,
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
        sendInput("\x1b[13;2u", true);
        return false;
      }
      return true;
    });

    // While a replay-flagged chunk parses, xterm's onData carries its synthesized answers to device queries
    // replayed from scrollback (ESC[6n etc.) — already answered in a previous life, so they must not reach the
    // child as input, where they'd echo as garbage (^[[19;23R) at the prompt. Suppressed for exactly the parse
    // window of each such chunk (write callbacks fire in order), then live queries get answered as normal.
    // Only the answer shapes are dropped: real keystrokes typed during the window still reach the child.
    let replaysParsing = 0;
    term.onData((data) => {
      if (isReplayedQueryAnswer(data)) {
        if (replaysParsing === 0) {
          sendInput(data, false);
        }
        return;
      }

      sendInput(data, true);
    });

    term.onResize(({ cols, rows }) => {
      keepNativeTouchTarget();
      messages.publish("resize", { columns: cols, rows });
    });

    // Register this pane's focus fn so the layout can land keyboard focus here (Ctrl+N / focus-pane).
    props.onFocusReady?.(() => term.focus());

    // Xterm's document gesture listener cancels long-press; its own cursor textarea keeps native paste local.
    const stopNativeTouch = (event: TouchEvent): void => event.stopPropagation();
    const onNativePaste = (event: ClipboardEvent): void => {
      if (event.clipboardData !== null) {
        event.preventDefault();
      }
    };
    const onNativePasteInput = (event: InputEvent): void => {
      if (event.inputType === "insertFromPaste" && textarea.value.length > 0) {
        term.paste(textarea.value);
        textarea.value = "";
      }
    };
    const nativeTouchEvents = ["touchstart", "touchmove", "touchend", "touchcancel"] as const;
    if (nativeTouchPaste) {
      for (const event of nativeTouchEvents) {
        textarea.addEventListener(event, stopNativeTouch);
      }
      textarea.addEventListener("compositionstart", keepNativeTouchTarget);
      textarea.addEventListener("paste", onNativePaste, true);
      textarea.addEventListener("input", onNativePasteInput);
    }

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

    const offOutput = messages.on<{ dataB64: string; replay: boolean }>(
      "output",
      ({ dataB64, replay }) => {
        if (replay) {
          term.write(base64ToBytes(dataB64), () => {
            replaysParsing--;
          });
          replaysParsing++;
        } else {
          term.write(base64ToBytes(dataB64));
        }
      },
    );
    const offExit = messages.on<{ code: number }>("exit", ({ code }) => {
      term.write(`\r\n\x1b[90m[process exited: ${code}]\x1b[0m\r\n`);
    });
    const offReset = messages.on<{ respawn: boolean }>("reset", ({ respawn }) => {
      if (respawn) {
        term.reset();
      } else {
        term.write("\x1b[H\x1b[2J\x1b[3J");
      }
      messages.publish("ready", { columns: term.cols, rows: term.rows });
    });

    onCleanup(() => {
      disposed = true;
      renderSub.dispose();
      offOutput();
      offExit();
      offReset();
      offFonts();
      offEditorOptions();
      offTheme();
      offRegister();
      offSelectionSource();
      selectionSub.dispose();
      offClipboard.dispose();
      offImagePaste();
      offCwd.dispose();
      if (nativeTouchPaste) {
        for (const event of nativeTouchEvents) {
          textarea.removeEventListener(event, stopNativeTouch);
        }
        textarea.removeEventListener("compositionstart", keepNativeTouchTarget);
        textarea.removeEventListener("paste", onNativePaste, true);
        textarea.removeEventListener("input", onNativePasteInput);
      }
      container.removeEventListener("focusin", onContainerFocus);
      resizeObserver.disconnect();
      window.removeEventListener("resize", refit);
      if (import.meta.hot) {
        import.meta.hot.off("vite:afterUpdate", onHmrUpdate);
      }
      if (window.__WEAVIE_TERMINALS__?.[termKey] === term) {
        delete window.__WEAVIE_TERMINALS__[termKey];
      }
      cancelWebglDispose();
      webgl?.dispose();
      nativeCursorSub?.dispose();
      disposeTouch();
      term.dispose();
    });

    // Subscribe and register cleanup before ready starts the child, so its first output has a live consumer.
    messages.publish("ready", { columns: term.cols, rows: term.rows });
  });

  return (
    <div
      class="term"
      classList={{ "terminal-native-touch-paste": nativeTouchPaste }}
      ref={container}
      role="application"
      onContextMenu={(event) => {
        if (nativeTouchPaste || props.onContextMenu === undefined) {
          return;
        }
        event.preventDefault();
        term.focus(); // make this the focused terminal so the menu's copy/paste/clear act on it
        props.onContextMenu(event, hoveredUrl());
      }}
    />
  );
}
