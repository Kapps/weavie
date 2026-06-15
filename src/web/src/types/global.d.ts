// Ambient declarations for the WKWebView bridge surface.

interface WeavieWebkitHandler {
  postMessage(body: string): void;
}

interface Window {
  webkit?: {
    messageHandlers?: {
      weavie?: WeavieWebkitHandler;
    };
  };
  /** Entry point the C# host calls via EvaluateJavaScript to push messages into the page. */
  __weavieReceive?: (raw: string) => void;
}
