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
  /** Build identity (SemVer with the build number as patch, e.g. "0.1.247"); shown read-only as the title-bar badge. */
  buildNumber: string;
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
   * Bridge WebSocket advertised by a headless "serve" host: a concrete `ws://host:port/path` URL, or "auto"
   * to derive it same-origin. Absent in the native shells (messageHandlers channel) and plain-browser dev.
   */
  __WEAVIE_BRIDGE_WS__?: string;
  /** Custom-chrome config injected by the Windows host; drives the web title bar. */
  __WEAVIE_SHELL__?: WeavieShellConfig;
  /** Recents injected by the host for the standalone welcome window (welcome.html). */
  __WEAVIE_WELCOME__?: WeavieWelcomeConfig;
  /** Live xterm terminals keyed by "slot:pane", published by TerminalView for e2e / diagnostics (read-only). */
  __WEAVIE_TERMINALS__?: Record<string, import("@xterm/xterm").Terminal>;
  /** The live Monaco editor, published by createEditor for e2e / diagnostics (read-only). */
  __WEAVIE_EDITOR__?: import("monaco-editor").editor.IStandaloneCodeEditor;
  // The LSP bridge config (window.__WEAVIE_LSP__) is augmented onto Window in lsp/lsp-client.ts.
}
