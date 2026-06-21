// Ambient declarations for the WKWebView bridge surface.

interface WeavieWebkitHandler {
  postMessage(body: string): void;
}

/** Host-injected shell config (window.__WEAVIE_SHELL__), set before navigation; absent in plain-browser dev. */
interface WeavieShellConfig {
  /** Short platform id, e.g. "win" or "mac". */
  platform: string;
  /** Title-bar mode the web should render ("custom"), or undefined/null for the host's native chrome. */
  titleBar?: string | null;
  /** The window's workspace label (folder leaf name). */
  workspaceLabel: string;
  /** Recent workspace paths (absolute); the web derives leaf names for the File ▸ Open Recent submenu. */
  recents: string[];
}

/** Host-injected config for the standalone welcome window (window.__WEAVIE_WELCOME__), set before navigation. */
interface WeavieWelcomeConfig {
  /** Recent workspace paths (absolute); the welcome screen derives leaf names for display. */
  recents: string[];
}

interface Window {
  webkit?: {
    messageHandlers?: {
      weavie?: WeavieWebkitHandler;
    };
  };
  /** Entry point the C# host calls via EvaluateJavaScript to push messages into the page. */
  __weavieReceive?: (raw: string) => void;
  /**
   * Remote/web Weavie: a headless "serve" host advertises its bridge WebSocket here (set before
   * navigation, like __WEAVIE_FONTS__). A concrete `ws://host:port/path` URL, or the literal "auto"
   * to derive it same-origin from `location`. Absent in the native shells (they use the in-process
   * `webkit.messageHandlers` channel) and in plain-browser dev (where the bridge stays a no-op).
   */
  __WEAVIE_BRIDGE_WS__?: string;
  /** Custom-chrome config injected by the Windows host; drives the web title bar. */
  __WEAVIE_SHELL__?: WeavieShellConfig;
  /** Recents injected by the host for the standalone welcome window (welcome.html). */
  __WEAVIE_WELCOME__?: WeavieWelcomeConfig;
  /**
   * Live xterm terminals by session ("claude" | "shell"), published by TerminalView for e2e /
   * diagnostics introspection (read-only). Lets a test assert per-pane terminal state — e.g.
   * `modes.mouseTrackingMode`, which must survive a session switch for the wheel to reach Claude.
   */
  __WEAVIE_TERMINALS__?: Record<string, import("@xterm/xterm").Terminal>;
  // The LSP bridge config (window.__WEAVIE_LSP__) is augmented onto Window in lsp/lsp-client.ts.
}
