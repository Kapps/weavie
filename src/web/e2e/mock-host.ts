import { readFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import { extname, join, normalize } from "node:path";
import { type WebSocket, WebSocketServer } from "ws";
import { basename } from "../src/editor/fs-path";

export interface SessionAddress {
  slot: string;
  incarnation: string;
}

export interface MockSession {
  id: string;
  label: string;
  address: SessionAddress | null;
  loaded: boolean;
  providerId: "claude" | "codex";
  agentSurface: "terminal" | "structured";
  agentInputProtocol: number;
  status: "starting" | "working" | "needsInput" | "idle" | "waiting" | "error";
  hue: number;
  monogram: string;
}

export interface MockLiveSession extends MockSession {
  address: SessionAddress;
  loaded: true;
}

export interface MessageEnvelope {
  scope: "host" | "session";
  session: SessionAddress | null;
  kind: "event" | "request" | "response" | "cancel";
  requestId: string | null;
  feature: string;
  name: string;
  payload: unknown;
  error: string | null;
}

interface MessageSelector {
  scope: "host" | "session";
  session: SessionAddress | null;
  kind: MessageEnvelope["kind"];
  feature: string;
  name: string;
}

interface MessageWaiter {
  selector: MessageSelector;
  after: number;
  resolve: (message: MessageEnvelope) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

export function mockSession(
  id: string,
  label: string,
  providerId: "claude" | "codex",
): MockLiveSession {
  return {
    id,
    label,
    address: { slot: id, incarnation: `${id}-incarnation` },
    loaded: true,
    providerId,
    agentSurface: providerId === "codex" ? "structured" : "terminal",
    agentInputProtocol: providerId === "codex" ? 2 : 0,
    status: "idle",
    hue: 200,
    monogram: label.slice(0, 1).toUpperCase(),
  };
}

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
  ".map": "application/json",
};

const DEFAULT_LAYOUT = {
  root: {
    type: "split",
    dir: "row",
    weights: [0.4, 0.6],
    children: [
      {
        type: "split",
        dir: "column",
        weights: [0.5, 0.5],
        children: [
          { type: "pane", id: "p_agent", kind: "terminal:claude" },
          { type: "pane", id: "p_shell", kind: "terminal:shell" },
        ],
      },
      { type: "pane", id: "p_editor", kind: "editor" },
    ],
  },
  focused: "p_agent",
};

export interface MockHostOptions {
  distDir: string;
  files?: Record<string, string>;
  sessions?: MockSession[];
  commandCatalog?: { commands: unknown[]; keybindings: unknown[] };
}

interface AgentHistoryState {
  generation: number;
  messages: Record<string, unknown>[];
  pageSize: number;
}

interface AgentHistoryRead {
  generation: number;
  pageSize: number;
  records: Array<{ message: Record<string, unknown>; ordinal: number; revision: number }>;
  revision: number;
}

export class MockHost {
  readonly received: MessageEnvelope[] = [];
  readonly mediaRequests: Array<{ session: string; path: string; status: number }> = [];
  readonly files: Map<string, string>;

  private readonly media = new Map<string, Buffer>();
  private readonly distDir: string;
  private readonly commandCatalog: { commands: unknown[]; keybindings: unknown[] };
  private readonly http: Server;
  private readonly wss: WebSocketServer;
  private readonly waiters: MessageWaiter[] = [];
  private readonly handlers = new Set<{
    selector: MessageSelector;
    handler: (message: MessageEnvelope) => void;
  }>();
  private readonly agentHistories = new Map<string, AgentHistoryState>();
  private readonly agentHistoryReads = new Map<string, AgentHistoryRead>();
  private readonly agentItems = new Map<string, Map<string, number>>();
  private readonly agentOrdinals = new Map<string, number>();
  private readonly agentRevisions = new Map<string, number>();
  private readonly pausedAgentHistoryRequests: MessageEnvelope[] = [];
  private readonly pausedFileRequests: MessageEnvelope[] = [];
  private socket: WebSocket | null = null;
  private sessions: MockSession[];
  private pendingHello: MessageEnvelope | null = null;
  private helloPaused = false;
  private fileProviderPaused = false;
  private agentHistoryPauseAfterResponses: number | null = null;
  private agentHistoryResponses = 0;
  private agentHistoryReadSequence = 0;
  private requestSequence = 0;
  private port = 0;

  private constructor(
    distDir: string,
    files: Record<string, string>,
    sessions: MockSession[],
    commandCatalog: { commands: unknown[]; keybindings: unknown[] },
  ) {
    this.distDir = distDir;
    this.commandCatalog = commandCatalog;
    this.files = new Map(Object.entries(files));
    this.sessions = sessions;
    this.http = createServer(
      (req, res) => void this.serveStatic(req.url ?? "/", req.method ?? "GET", res),
    );
    this.wss = new WebSocketServer({ server: this.http, path: "/weavie-bridge" });
    this.wss.on("connection", (socket) => this.onConnection(socket));
  }

  static async start(options: MockHostOptions): Promise<MockHost> {
    const host = new MockHost(
      options.distDir,
      options.files ?? {},
      options.sessions ?? [],
      options.commandCatalog ?? { commands: [], keybindings: [] },
    );
    await new Promise<void>((resolve) => host.http.listen(0, "127.0.0.1", resolve));
    const address = host.http.address();
    if (address === null || typeof address === "string") {
      throw new Error("mock host failed to bind a TCP port");
    }
    host.port = address.port;
    return host;
  }

  get url(): string {
    return `http://127.0.0.1:${this.port}`;
  }

  get bridgeUrl(): string {
    return `ws://127.0.0.1:${this.port}/weavie-bridge`;
  }

  pageUrl(path = "/"): string {
    return `${this.url}${path}?weavie-bridge=${encodeURIComponent(this.bridgeUrl)}`;
  }

  checkpoint(): number {
    return this.received.length;
  }

  address(slot: string): SessionAddress {
    const address = this.sessions.find((session) => session.id === slot)?.address;
    if (address === undefined || address === null) {
      throw new Error(`mock host has no live session '${slot}'`);
    }
    return address;
  }

  setSessions(sessions: MockSession[]): void {
    this.sessions = sessions;
    if (this.socket !== null && this.socket.readyState === this.socket.OPEN) {
      this.publishHost("sessions", "catalog", sessions);
    }
  }

  setMedia(sessionIncarnation: string, path: string, bytes: Buffer): void {
    this.media.set(JSON.stringify([sessionIncarnation, path]), bytes);
  }

  setAgentHistory(session: string | SessionAddress, history: AgentHistoryState): void {
    const address = typeof session === "string" ? this.address(session) : session;
    const key = this.addressKey(address);
    this.agentHistories.set(key, history);
    this.agentItems.delete(key);
    this.agentOrdinals.set(key, history.messages.length);
    this.agentRevisions.set(key, history.messages.length);
  }

  pauseAgentHistoryAfterResponses(responses: number): void {
    this.agentHistoryPauseAfterResponses = responses;
    this.agentHistoryResponses = 0;
  }

  resumeAgentHistory(): void {
    this.agentHistoryPauseAfterResponses = null;
    for (const request of this.pausedAgentHistoryRequests.splice(0)) {
      this.answerAgentHistory(request);
    }
  }

  publishHost(feature: string, name: string, payload: unknown): void {
    this.send({
      scope: "host",
      session: null,
      kind: "event",
      requestId: null,
      feature,
      name,
      payload,
      error: null,
    });
  }

  publishSession(
    session: string | SessionAddress,
    feature: string,
    name: string,
    payload: unknown,
  ): void {
    this.send(this.sessionEvent(session, feature, name, payload));
  }

  publishAgentPane(session: string | SessionAddress, message: Record<string, unknown>): void {
    const address = typeof session === "string" ? this.address(session) : session;
    this.publishSession(address, "agent", "pane", this.agentMessage(address, message));
  }

  publishAgentPaneBatch(
    session: string | SessionAddress,
    messages: Record<string, unknown>[],
  ): void {
    const address = typeof session === "string" ? this.address(session) : session;
    this.publishSession(address, "agent", "paneBatch", {
      messages: messages.map((message) => this.agentMessage(address, message)),
    });
  }

  private sessionEvent(
    session: string | SessionAddress,
    feature: string,
    name: string,
    payload: unknown,
  ): MessageEnvelope {
    return {
      scope: "session",
      session: typeof session === "string" ? this.address(session) : session,
      kind: "event",
      requestId: null,
      feature,
      name,
      payload,
      error: null,
    };
  }

  requestSession(
    session: string | SessionAddress,
    feature: string,
    name: string,
    payload: unknown,
  ): Promise<MessageEnvelope> {
    const address = typeof session === "string" ? this.address(session) : session;
    const requestId = `mock-${++this.requestSequence}`;
    const response = this.waitFor({
      scope: "session",
      session: address,
      kind: "response",
      feature,
      name,
    });
    this.send({
      scope: "session",
      session: address,
      kind: "request",
      requestId,
      feature,
      name,
      payload,
      error: null,
    });
    return response;
  }

  respond(request: MessageEnvelope, payload: unknown): void {
    if (request.kind !== "request" || request.requestId === null) {
      throw new Error("only a request envelope can be answered");
    }
    this.send(this.response(request, payload));
  }

  private response(request: MessageEnvelope, payload: unknown): MessageEnvelope {
    return {
      scope: request.scope,
      session: request.session,
      kind: "response",
      requestId: request.requestId,
      feature: request.feature,
      name: request.name,
      payload,
      error: null,
    };
  }

  waitUntilConnected(after = 0): Promise<MessageEnvelope> {
    return this.waitForHost("request", "connection", "hello", after);
  }

  waitForHost(
    kind: MessageEnvelope["kind"],
    feature: string,
    name: string,
    after = 0,
  ): Promise<MessageEnvelope> {
    return this.waitFor({ scope: "host", session: null, kind, feature, name }, after);
  }

  waitForSession(
    session: string | SessionAddress,
    kind: MessageEnvelope["kind"],
    feature: string,
    name: string,
    after = 0,
  ): Promise<MessageEnvelope> {
    return this.waitFor(
      {
        scope: "session",
        session: typeof session === "string" ? this.address(session) : session,
        kind,
        feature,
        name,
      },
      after,
    );
  }

  onSession(
    session: string | SessionAddress,
    kind: MessageEnvelope["kind"],
    feature: string,
    name: string,
    handler: (message: MessageEnvelope) => void,
  ): () => void {
    const subscription = {
      selector: {
        scope: "session" as const,
        session: typeof session === "string" ? this.address(session) : session,
        kind,
        feature,
        name,
      },
      handler,
    };
    this.handlers.add(subscription);
    return () => this.handlers.delete(subscription);
  }

  onHost(
    kind: MessageEnvelope["kind"],
    feature: string,
    name: string,
    handler: (message: MessageEnvelope) => void,
  ): () => void {
    const subscription = {
      selector: { scope: "host" as const, session: null, kind, feature, name },
      handler,
    };
    this.handlers.add(subscription);
    return () => this.handlers.delete(subscription);
  }

  pauseHello(): void {
    this.helloPaused = true;
  }

  resumeHello(): void {
    this.helloPaused = false;
    if (this.pendingHello !== null) {
      const request = this.pendingHello;
      this.pendingHello = null;
      this.respond(request, this.hello());
    }
  }

  pauseFileProvider(): void {
    this.fileProviderPaused = true;
  }

  resumeFileProvider(): void {
    this.fileProviderPaused = false;
    for (const request of this.pausedFileRequests.splice(0)) {
      this.answerFileProvider(request);
    }
  }

  disconnectBridge(): void {
    this.socket?.terminate();
  }

  async close(): Promise<void> {
    this.socket?.terminate();
    this.wss.close();
    for (const waiter of this.waiters.splice(0)) {
      clearTimeout(waiter.timer);
      waiter.reject(new Error("mock host closed"));
    }
    this.http.closeAllConnections();
    await new Promise<void>((resolve) => this.http.close(() => resolve()));
  }

  private onConnection(socket: WebSocket): void {
    this.socket = socket;
    socket.on("message", (data) => this.onMessage(String(data)));
    socket.on("close", () => this.agentHistoryReads.clear());
  }

  private onMessage(raw: string): void {
    let message: MessageEnvelope;
    try {
      message = JSON.parse(raw) as MessageEnvelope;
    } catch {
      return;
    }
    if (!validEnvelope(message)) {
      return;
    }
    const index = this.received.push(message) - 1;
    for (const subscription of [...this.handlers]) {
      if (matches(message, subscription.selector)) {
        subscription.handler(message);
      }
    }
    for (const waiter of [...this.waiters]) {
      if (index >= waiter.after && matches(message, waiter.selector)) {
        this.waiters.splice(this.waiters.indexOf(waiter), 1);
        clearTimeout(waiter.timer);
        waiter.resolve(message);
      }
    }

    if (
      message.kind === "request" &&
      message.scope === "host" &&
      message.feature === "connection" &&
      message.name === "hello"
    ) {
      if (this.helloPaused) {
        this.pendingHello = message;
      } else {
        this.respond(message, this.hello());
      }
      return;
    }
    if (
      message.kind === "request" &&
      message.scope === "session" &&
      message.feature === "lifecycle" &&
      message.name === "sync"
    ) {
      this.respond(message, { ok: true });
      return;
    }
    if (
      message.kind === "request" &&
      message.scope === "session" &&
      message.feature === "agent" &&
      message.name === "historyPage"
    ) {
      this.answerAgentHistory(message);
      return;
    }
    if (
      message.kind === "event" &&
      message.scope === "session" &&
      message.feature === "agent" &&
      message.name === "historyClose"
    ) {
      const readId = (message.payload as { readId?: unknown }).readId;
      if (typeof readId === "string") {
        this.agentHistoryReads.delete(readId);
      }
      return;
    }
    this.answerFileProvider(message);
  }

  private answerAgentHistory(request: MessageEnvelope): void {
    if (request.session === null) {
      return;
    }
    if (
      this.agentHistoryPauseAfterResponses !== null &&
      this.agentHistoryResponses >= this.agentHistoryPauseAfterResponses
    ) {
      this.pausedAgentHistoryRequests.push(request);
      return;
    }
    this.agentHistoryResponses++;
    const supplied = request.payload as {
      cursor?: {
        before?: number;
        jsonBefore?: number | null;
        readId?: string;
      } | null;
      knownGeneration?: number | null;
      knownRevision?: number | null;
    };
    const readId = supplied.cursor?.readId ?? `history-${++this.agentHistoryReadSequence}`;
    let read = this.agentHistoryReads.get(readId);
    if (read === undefined) {
      const current = this.agentHistories.get(this.addressKey(request.session)) ?? {
        generation: 0,
        messages: [],
        pageSize: 100,
      };
      const revision = current.messages.length;
      const afterRevision =
        supplied.knownGeneration === current.generation ? (supplied.knownRevision ?? 0) : 0;
      read = {
        generation: current.generation,
        pageSize: current.pageSize,
        records: current.messages.flatMap((message, index) => {
          const recordRevision = index + 1;
          return recordRevision > afterRevision
            ? [{ message, ordinal: recordRevision, revision: recordRevision }]
            : [];
        }),
        revision,
      };
      this.agentHistoryReads.set(readId, read);
    }
    const before = supplied.cursor?.before ?? read.records.length;
    const start = Math.max(0, before - read.pageSize);
    const records = read.records.slice(start, before).map((record) => ({
      ...record.message,
      generation: read.generation,
      ordinal: record.ordinal,
      revision: record.revision,
      textOffset: 0,
      textLength: typeof record.message.text === "string" ? record.message.text.length : 0,
    }));
    const payload = {
      generation: read.generation,
      readId,
      revision: read.revision,
      messages: records.map((record) => {
        const json = JSON.stringify(record);
        return {
          generation: record.generation,
          ordinal: record.ordinal,
          revision: record.revision,
          jsonOffset: 0,
          jsonLength: json.length,
          json,
        };
      }),
      cursor:
        start === 0
          ? null
          : {
              readId,
              before: start,
              jsonBefore: null,
            },
    };
    if (payload.cursor === null) {
      this.agentHistoryReads.delete(readId);
    }
    this.send(this.response(request, payload));
  }

  private answerFileProvider(message: MessageEnvelope): void {
    if (
      message.kind !== "request" ||
      message.scope !== "session" ||
      message.feature !== "files" ||
      !["stat", "read", "write"].includes(message.name)
    ) {
      return;
    }
    if (this.fileProviderPaused) {
      this.pausedFileRequests.push(message);
      return;
    }
    const payload = message.payload as { path?: unknown; content?: unknown };
    const path = String(payload.path ?? "");
    const content = this.files.get(path);
    const stat = {
      exists: content !== undefined,
      isDirectory: false,
      mtimeMs: content === undefined ? 0 : 1,
      ctimeMs: content === undefined ? 0 : 1,
      size: content?.length ?? 0,
    };
    if (message.name === "stat") {
      this.respond(message, stat);
    } else if (message.name === "read") {
      this.respond(message, {
        ok: content !== undefined,
        content: content ?? null,
        stat,
        code: content === undefined ? "FileNotFound" : null,
        error: null,
      });
    } else {
      const next = String(payload.content ?? "");
      this.files.set(path, next);
      this.respond(message, {
        ok: true,
        stat: {
          exists: true,
          isDirectory: false,
          mtimeMs: 2,
          ctimeMs: 1,
          size: next.length,
        },
        error: null,
      });
    }
  }

  private send(message: MessageEnvelope): void {
    this.sendText(JSON.stringify(message));
  }

  private sendText(data: string): void {
    if (this.socket === null || this.socket.readyState !== this.socket.OPEN) {
      throw new Error("mock host has no connected page");
    }
    this.socket.send(data);
  }

  private addressKey(address: SessionAddress): string {
    return `${address.slot}\0${address.incarnation}`;
  }

  private agentMessage(
    address: SessionAddress,
    message: Record<string, unknown>,
  ): Record<string, unknown> {
    const key = this.addressKey(address);
    const itemId = typeof message.itemId === "string" ? message.itemId : "";
    const itemKey =
      itemId.length === 0
        ? null
        : JSON.stringify([message.threadId ?? null, message.turnId ?? null, itemId]);
    let ordinal = itemKey === null ? undefined : this.agentItems.get(key)?.get(itemKey);
    if (ordinal === undefined) {
      ordinal = (this.agentOrdinals.get(key) ?? 0) + 1;
      this.agentOrdinals.set(key, ordinal);
      if (itemKey !== null && message.type !== "item-completed") {
        let items = this.agentItems.get(key);
        if (items === undefined) {
          items = new Map<string, number>();
          this.agentItems.set(key, items);
        }
        items.set(itemKey, ordinal);
      }
    } else if (message.type === "item-completed") {
      if (itemKey !== null) {
        this.agentItems.get(key)?.delete(itemKey);
      }
    }
    const revision = (this.agentRevisions.get(key) ?? 0) + 1;
    this.agentRevisions.set(key, revision);
    const text = typeof message.text === "string" ? message.text : null;
    return {
      ...message,
      generation: this.agentHistories.get(key)?.generation ?? 0,
      ordinal,
      revision,
      textOffset: 0,
      textLength: text?.length ?? 0,
    };
  }

  private waitFor(selector: MessageSelector, after = 0): Promise<MessageEnvelope> {
    const existing = this.received.find(
      (message, index) => index >= after && matches(message, selector),
    );
    if (existing !== undefined) {
      return Promise.resolve(existing);
    }
    const timeoutMs = process.platform === "linux" ? 15_000 : 30_000;
    return new Promise<MessageEnvelope>((resolve, reject) => {
      const waiter: MessageWaiter = {
        selector,
        after,
        resolve,
        reject,
        timer: setTimeout(() => {
          this.waiters.splice(this.waiters.indexOf(waiter), 1);
          reject(
            new Error(
              `timed out waiting for ${selector.scope} ${selector.kind} ${selector.feature}.${selector.name}`,
            ),
          );
        }, timeoutMs),
      };
      this.waiters.push(waiter);
    });
  }

  private hello() {
    return {
      hostIncarnation: "mock-host",
      buildNumber: "test",
      sessions: this.sessions,
      layout: DEFAULT_LAYOUT,
      remoteAgents: [],
      rail: { lastLocation: "local", promoted: [], selected: null },
      search: {
        options: {
          caseSensitive: false,
          wholeWord: false,
          regex: false,
          excludeGitignored: true,
          include: "",
          exclude: "",
        },
        recentTerms: [],
      },
      testProfile: "",
      commandCatalog: this.commandCatalog,
    };
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
          .end(JSON.stringify({ url: `${this.url}/index.html`, token: "mock" }));
      }
      return;
    }
    if (pathname.startsWith("/weavie-media/")) {
      const session = request.searchParams.get("session") ?? "";
      const path = request.searchParams.get("path") ?? "";
      const routeMatches = pathname === `/weavie-media/${encodeURIComponent(basename(path))}`;
      const body = routeMatches ? this.media.get(JSON.stringify([session, path])) : undefined;
      const status = body === undefined ? 404 : 200;
      this.mediaRequests.push({ session, path, status });
      res
        .writeHead(status, { "content-type": "image/png", "access-control-allow-origin": "*" })
        .end(body ?? "not found");
      return;
    }

    const relative = pathname === "/" ? "index.html" : pathname.replace(/^\/+/, "");
    const resolved = normalize(join(this.distDir, relative));
    if (!resolved.startsWith(normalize(this.distDir))) {
      res.writeHead(403).end("forbidden");
      return;
    }
    try {
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

function validEnvelope(value: MessageEnvelope): boolean {
  return (
    (value.scope === "host" || value.scope === "session") &&
    ["event", "request", "response", "cancel"].includes(value.kind) &&
    typeof value.feature === "string" &&
    typeof value.name === "string" &&
    (value.scope === "session") === (value.session !== null)
  );
}

function matches(message: MessageEnvelope, selector: MessageSelector): boolean {
  return (
    message.scope === selector.scope &&
    message.kind === selector.kind &&
    message.feature === selector.feature &&
    message.name === selector.name &&
    sameAddress(message.session, selector.session)
  );
}

function sameAddress(left: SessionAddress | null, right: SessionAddress | null): boolean {
  return left === null || right === null
    ? left === right
    : left.slot === right.slot && left.incarnation === right.incarnation;
}

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

function injectBootstrap(html: string): string {
  const script = `<script>${Object.entries(BOOTSTRAP_GLOBALS)
    .map(([name, value]) => `window.${name}=${JSON.stringify(value)};`)
    .join("")}</script>`;
  return html.includes("<head>") ? html.replace("<head>", `<head>${script}`) : script + html;
}
