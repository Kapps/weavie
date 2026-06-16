// Ambient declarations for the WKWebView bridge surface.

interface WeavieWebkitHandler {
  postMessage(body: string): void;
}

/** LSP bridge discovery the C# host injects (loopback WS endpoint + per-session token + workspace). */
interface WeavieLspConfig {
  url: string;
  token: string;
  workspace: string;
}

interface Window {
  webkit?: {
    messageHandlers?: {
      weavie?: WeavieWebkitHandler;
    };
  };
  /** Entry point the C# host calls via EvaluateJavaScript to push messages into the page. */
  __weavieReceive?: (raw: string) => void;
  /** LSP bridge config injected by the host before navigation; absent in a plain-browser dev run. */
  __WEAVIE_LSP__?: WeavieLspConfig;
}
