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

interface Window {
  webkit?: {
    messageHandlers?: {
      weavie?: WeavieWebkitHandler;
    };
  };
  /** Entry point the C# host calls via EvaluateJavaScript to push messages into the page. */
  __weavieReceive?: (raw: string) => void;
  /** Custom-chrome config injected by the Windows host; drives the web title bar. */
  __WEAVIE_SHELL__?: WeavieShellConfig;
  // The LSP bridge config (window.__WEAVIE_LSP__) is augmented onto Window in lsp/lsp-client.ts.
}
