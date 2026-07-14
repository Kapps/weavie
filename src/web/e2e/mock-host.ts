// Minimal stand-in for the headless "serve" host, for testing the web app's remote bridge transport in a
// real browser. It serves the built web app (dist/) over HTTP and speaks the bridge protocol over a
// WebSocket at /weavie-bridge — the same HostBound/WebBound JSON the native shells exchange. It records
// everything the page sends, lets a test push any web-bound message, and answers the file:// provider
// (fs-stat / fs-read / fs-write) from an in-memory file map. No claude, filesystem, or LSP.

import { readFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import { extname, join, normalize } from "node:path";
import { type WebSocket, WebSocketServer } from "ws";

/** A bridge message in either direction — kept loose on purpose; the web side owns the real types. */
type Message = { type: string } & Record<string, unknown>;

const MIME: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".wasm": "application/wasm",
  ".woff": "font/woff",
  ".woff2": "font/woff2",
  ".ttf": "font/ttf",
  ".map": "application/json; charset=utf-8",
};

export interface MockHostOptions {
  /** Absolute path to the built web app (the Vite `dist/` directory) to serve. */
  distDir: string;
  /** Seed files the host-backed file provider can read, keyed by absolute native path. */
  files?: Record<string, string>;
  /** Ready replay protocol advertised by the host; 0 models workers predating bridge-ready. */
  readyReplayProtocol?: 0 | 1;
}

/** A running mock host: an HTTP server for the app plus a WebSocket bridge endpoint. */
export class MockHost {
  /** Every message the page sent to the host, in arrival order. */
  readonly received: Message[] = [];
  /** Every streamed-media request, including rejected mixed backend/session/path identities. */
  readonly mediaRequests: Array<{ session: string; path: string; status: number }> = [];
  /** Files the fs-* provider serves, keyed by path; tests can mutate between steps. */
  readonly files: Map<string, string>;
  private readonly media = new Map<string, Buffer>();

  private readonly distDir: string;
  private readonly readyReplayProtocol: 0 | 1;
  private readonly http: Server;
  private readonly wss: WebSocketServer;
  private socket: WebSocket | null = null;
  private bridgeReadyPaused = false;
  private bridgeId = "";
  private readonly waiters: { type: string; resolve: (m: Message) => void }[] = [];
  private port = 0;

  private constructor(distDir: string, files: Record<string, string>, readyReplayProtocol: 0 | 1) {
    this.distDir = distDir;
    this.readyReplayProtocol = readyReplayProtocol;
    this.files = new Map(Object.entries(files));
    this.http = createServer(
      (req, res) => void this.serveStatic(req.url ?? "/", req.method ?? "GET", res),
    );
    this.wss = new WebSocketServer({ server: this.http, path: "/weavie-bridge" });
    this.wss.on("connection", (ws) => this.onConnection(ws));
  }

  /** Starts a mock host on an ephemeral port and resolves once it is accepting connections. */
  static async start(options: MockHostOptions): Promise<MockHost> {
    const host = new MockHost(
      options.distDir,
      options.files ?? {},
      options.readyReplayProtocol ?? 1,
    );
    await new Promise<void>((resolve) => host.http.listen(0, "127.0.0.1", resolve));
    const address = host.http.address();
    if (address === null || typeof address === "string") {
      throw new Error("mock host failed to bind a TCP port");
    }
    host.port = address.port;
    return host;
  }

  /** The HTTP base, e.g. http://127.0.0.1:54321. */
  get url(): string {
    return `http://127.0.0.1:${this.port}`;
  }

  /** The bridge WebSocket URL to hand the page via `?weavie-bridge=`. */
  get bridgeUrl(): string {
    return `ws://127.0.0.1:${this.port}/weavie-bridge`;
  }

  /** The full page URL with the bridge transport wired in (the way a test should navigate). */
  pageUrl(path = "/"): string {
    return `${this.url}${path}?weavie-bridge=${encodeURIComponent(this.bridgeUrl)}`;
  }

  /** Allows one exact authenticated media identity to stream from this backend. */
  setMedia(session: string, path: string, bytes: Buffer): void {
    this.media.set(JSON.stringify([session, path]), bytes);
  }

  /** Pushes a web-bound message (host -> page) over the live bridge socket. */
  pushToWeb(message: Message): void {
    if (this.socket === null || this.socket.readyState !== this.socket.OPEN) {
      throw new Error("pushToWeb: no page is connected to the bridge yet");
    }
    this.socket.send(JSON.stringify(message));
  }

  /** Holds the ordered ready-tail marker so reconnect UI can be asserted while state replay is incomplete. */
  pauseBridgeReady(): void {
    this.bridgeReadyPaused = true;
  }

  /** Releases a held ready-tail marker to mark the current bridge replay complete. */
  resumeBridgeReady(): void {
    this.bridgeReadyPaused = false;
    this.pushToWeb({ type: "bridge-ready", bridgeId: this.bridgeId });
  }

  /** Drops only the live bridge socket; the server stays up so the page reconnects normally. */
  disconnectBridge(): void {
    this.socket?.terminate();
  }

  /** Resolves with the next (or already-received) host-bound message of the given type. */
  waitForMessage(type: string, timeoutMs = 15_000): Promise<Message> {
    const existing = this.received.find((m) => m.type === type);
    if (existing !== undefined) {
      return Promise.resolve(existing);
    }
    return new Promise<Message>((resolve, reject) => {
      const timer = setTimeout(
        () => reject(new Error(`timed out after ${timeoutMs}ms waiting for "${type}"`)),
        timeoutMs,
      );
      this.waiters.push({
        type,
        resolve: (m) => {
          clearTimeout(timer);
          resolve(m);
        },
      });
    });
  }

  /** Stops the HTTP + WebSocket servers, force-closing any lingering sockets so teardown can't hang. */
  async close(): Promise<void> {
    this.socket?.terminate();
    this.wss.close();
    // http.close() fires its callback only once every connection has ended; the browser's keep-alive sockets
    // (still open while the page is, since afterEach runs before Playwright tears the page down) can outlive the
    // test on Windows loopback and stall it past the timeout. Force them shut so the close is deterministic
    // rather than waiting on the OS to reap them.
    this.http.closeAllConnections();
    await new Promise<void>((resolve) => this.http.close(() => resolve()));
  }

  private onConnection(ws: WebSocket): void {
    this.socket = ws;
    ws.on("message", (data) => this.onMessage(String(data)));
  }

  private onMessage(raw: string): void {
    let message: Message;
    try {
      message = JSON.parse(raw) as Message;
    } catch {
      return;
    }
    this.received.push(message);

    if (message.type === "ready") {
      this.bridgeId = typeof message.bridgeId === "string" ? message.bridgeId : "";
      this.pushToWeb(
        this.readyReplayProtocol === 1
          ? { type: "host-info", buildNumber: "test", readyReplayProtocol: 1 }
          : { type: "host-info", buildNumber: "test" },
      );
      if (this.readyReplayProtocol === 1 && !this.bridgeReadyPaused) {
        this.pushToWeb({ type: "bridge-ready", bridgeId: this.bridgeId });
      }
    }

    const waiter = this.waiters.find((w) => w.type === message.type);
    if (waiter !== undefined) {
      this.waiters.splice(this.waiters.indexOf(waiter), 1);
      waiter.resolve(message);
    }

    this.answerFileProvider(message);
  }

  // Answer the file:// provider so the editor can open working copies. Only fs-stat / fs-read / fs-write
  // are handled; everything else is recorded but not replied to.
  private answerFileProvider(message: Message): void {
    if (message.type === "fs-stat") {
      const content = this.files.get(String(message.path));
      this.pushToWeb(
        content === undefined
          ? {
              type: "fs-stat-result",
              id: message.id,
              ok: true,
              exists: false,
              isDir: false,
              mtimeMs: 0,
              ctimeMs: 0,
              size: 0,
            }
          : {
              type: "fs-stat-result",
              id: message.id,
              ok: true,
              exists: true,
              isDir: false,
              mtimeMs: 1,
              ctimeMs: 1,
              size: content.length,
            },
      );
    } else if (message.type === "fs-read") {
      const content = this.files.get(String(message.path));
      this.pushToWeb(
        content === undefined
          ? { type: "fs-read-result", id: message.id, ok: false, code: "FileNotFound" }
          : {
              type: "fs-read-result",
              id: message.id,
              ok: true,
              content,
              mtimeMs: 1,
              size: content.length,
            },
      );
    } else if (message.type === "fs-write") {
      this.files.set(String(message.path), String(message.content ?? ""));
      this.pushToWeb({
        type: "fs-write-result",
        id: message.id,
        ok: true,
        mtimeMs: 2,
        size: String(message.content ?? "").length,
      });
    }
  }

  private async serveStatic(
    rawUrl: string,
    method: string,
    res: import("node:http").ServerResponse,
  ): Promise<void> {
    const request = new URL(rawUrl, this.url);
    const pathname = request.pathname;
    if (pathname === "/backend") {
      const headers = {
        "access-control-allow-origin": "*",
        "access-control-allow-headers": "Authorization",
      };
      if (method === "OPTIONS") {
        res.writeHead(204, headers).end();
      } else {
        res
          .writeHead(200, { ...headers, "content-type": "application/json" })
          .end(JSON.stringify({ url: `${this.url}/index.html?token=mock` }));
      }
      return;
    }
    if (pathname === "/weavie-media") {
      const session = request.searchParams.get("session") ?? "";
      const path = request.searchParams.get("path") ?? "";
      const body = this.media.get(JSON.stringify([session, path]));
      const status = body === undefined ? 404 : 200;
      this.mediaRequests.push({ session, path, status });
      res
        .writeHead(status, { "content-type": "image/png", "access-control-allow-origin": "*" })
        .end(body ?? "not found");
      return;
    }

    const relative = pathname === "/" ? "index.html" : pathname.replace(/^\/+/, "");
    // Contain the served path inside distDir; a path that escapes is a 403.
    const resolved = normalize(join(this.distDir, relative));
    if (!resolved.startsWith(normalize(this.distDir))) {
      res.writeHead(403).end("forbidden");
      return;
    }
    try {
      // index.html gets the bootstrap globals injected before the module graph (like the real serve host's
      // Program.cs ServeIndexAsync); otherwise the build throws on the first host-injected global it reads
      // (see bridge.ts hostInjected). The bridge URL is left out — tests advertise it per-navigation via
      // `?weavie-bridge=` (see pageUrl).
      if (relative === "index.html") {
        const html = await readFile(resolved, "utf8");
        res
          .writeHead(200, { "content-type": "text/html; charset=utf-8" })
          .end(injectBootstrap(html));
        return;
      }

      const body = await readFile(resolved);
      res
        .writeHead(200, { "content-type": MIME[extname(resolved)] ?? "application/octet-stream" })
        .end(body);
    } catch {
      res.writeHead(404).end("not found");
    }
  }
}

// Bootstrap globals the build requires before navigation (bridge.ts hostInjected throws on any missing
// one). Minimal stand-ins for the real host's BuildBootstrapScript; `__WEAVIE_BRIDGE_WS__` is omitted so a
// navigation without `?weavie-bridge=` resolves to the "none" transport.
const FONT_SPEC = { family: "monospace", size: 13, weight: "normal" };
const BOOTSTRAP_GLOBALS: Record<string, unknown> = {
  __WEAVIE_FONTS__: { editor: FONT_SPEC, terminal: FONT_SPEC },
  __WEAVIE_NOTIFICATIONS__: {
    sounds: true,
    os: true,
    volume: 70,
    soundPack: "weavie",
    gates: { turnComplete: true, needsInput: true, failed: true },
  },
  __WEAVIE_EDITOR_OPTIONS__: {},
  __WEAVIE_THEME__: { mode: "system", light: { id: "weavie-light" }, dark: { id: "weavie-dark" } },
  __WEAVIE_COMMANDS__: [],
  __WEAVIE_KEYBINDINGS__: [],
  __WEAVIE_AGENT__: { defaultProvider: "claude" },
};

/** Injects the bootstrap globals right after <head> so they exist before the entry module runs. */
function injectBootstrap(html: string): string {
  const script = `<script>${Object.entries(BOOTSTRAP_GLOBALS)
    .map(([name, value]) => `window.${name}=${JSON.stringify(value)};`)
    .join("")}</script>`;
  return html.includes("<head>") ? html.replace("<head>", `<head>${script}`) : script + html;
}
