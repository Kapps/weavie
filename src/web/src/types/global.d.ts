// Ambient declarations for the native host bridge surface.

/** A window button the title bar draws; also the `window.control` action it sends. */
type WindowControlAction = "minimize" | "maximize-toggle" | "close";

/** What the host's window needs from the page's title bar (host-owned; re-pushed as `window.chrome`). */
interface WeavieWindowChrome {
  /** Whether the bar shows the application menus. */
  menus: boolean;
  /** How a press on empty bar space moves the window: not at all, CSS app-region, or a host-handled press. */
  caption: "none" | "region" | "press";
  /** Whether the page draws edge grab handles for a frameless window. */
  resizeHandles: boolean;
  /** The bar's height in CSS pixels, or null for the stylesheet default. */
  barHeight: number | null;
  /** The window buttons the page draws, in order, at each end of the bar. */
  controls: { start: WindowControlAction[]; end: WindowControlAction[] };
  /** Room left at each end for buttons the OS draws over the bar. */
  inset: { start: number; end: number };
}

/** Host-injected shell config (window.__WEAVIE_SHELL__), set before navigation; absent in plain-browser dev. */
interface WeavieShellConfig {
  /** Short platform id, e.g. "win" or "mac". */
  platform: string;
  /** What the title bar draws for this window. */
  chrome: WeavieWindowChrome;
  /** The window's workspace label (folder leaf name). */
  workspaceLabel: string;
  /** Recent workspace paths (absolute); the web derives leaf names for the File ▸ Open Recent submenu. */
  recents: string[];
  /** Build identity (public version plus internal build number, e.g. "0.2.1.1507"); shown read-only as the title-bar badge. */
  buildNumber: string;
}

/** Host-injected config for the standalone welcome window (window.__WEAVIE_WELCOME__), set before navigation. */
interface WeavieWelcomeConfig {
  /** Recent workspace paths (absolute); the welcome screen derives leaf names for display. */
  recents: string[];
  /** Whether Getting Started was finished or dismissed; the welcome screen shows it until then. */
  setupCompleted: boolean;
  /** What the welcome screen draws for its window; a strip only when the host hands it the caption. */
  chrome: WeavieWindowChrome;
}

interface Window {
  /** Removes the classic pre-module error capture once main.tsx owns runtime error reporting. */
  __WEAVIE_CLEAR_BOOT_ERROR_CAPTURE__?: () => void;
  /** Authenticated sender installed by the native host only in its app document. */
  __weaviePostMessage?: (raw: string) => void;
  /** Entry point the C# host calls via EvaluateJavaScript to push messages into the page. */
  __weavieReceive?: (raw: string) => void;
  /**
   * Bridge WebSocket advertised by a headless "serve" host: a concrete `ws://host:port/path` URL, or "auto"
   * to derive it same-origin. Absent in the native shells (authenticated script channel) and plain-browser dev.
   */
  __WEAVIE_BRIDGE_WS__?: string;
  /** Authenticated HTTP endpoint used directly by image/video elements for streamed workspace media. */
  __WEAVIE_RESOURCE_BASE__?: string;
  /** Shell config injected by every host before navigation; drives the title bar. */
  __WEAVIE_SHELL__?: WeavieShellConfig;
  /** Recents injected by the host for the standalone welcome window (welcome.html). */
  __WEAVIE_WELCOME__?: WeavieWelcomeConfig;
  /** Live xterms keyed by backend + exact session incarnation + pane for e2e diagnostics. */
  __WEAVIE_TERMINALS__?: Record<string, import("@xterm/xterm").Terminal>;
  /** The live Monaco editor, published by createEditor for e2e / diagnostics (read-only). */
  __WEAVIE_EDITOR__?: import("monaco-editor").editor.IStandaloneCodeEditor;
  /** The monaco namespace, published by createEditor so e2e can register providers (read-only). */
  __WEAVIE_MONACO__?: typeof import("monaco-editor");
  /**
   * The live review-walk file set + label the inline navigator cycles through, published by the editor
   * controller for e2e / diagnostics (read-only). A failed PR-switch test attaches this so the exact settled
   * set (a leaked cross-PR mix vs the correct set) is visible without walking the navigator. See fixtures.ts.
   */
  __WEAVIE_REVIEW__?: { files: string[]; label: string; rev: number };
}
