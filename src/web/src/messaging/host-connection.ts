import type { CommandInfo, ResolvedKeybinding } from "../commands/types";
import { ClientSessionState } from "./client-session-state";
import { MessageBus } from "./message-bus";
import type { MessageEnvelope, SessionAddress } from "./message-envelope";

export interface SessionCatalogEntry {
  id: string;
  label: string;
  address: SessionAddress | null;
  loaded: boolean;
  providerId: "claude" | "codex";
  agentSurface: "terminal" | "structured" | "unavailable";
  agentInputProtocol: number;
  status: "starting" | "working" | "needsInput" | "idle" | "waiting" | "error";
  hue: number;
  monogram: string;
}

export interface HostHello {
  hostIncarnation: string;
  buildNumber: string;
  sessions: SessionCatalogEntry[];
  layout: unknown;
  remoteAgents: { name: string; url: string; token: string }[];
  rail: {
    lastLocation: string;
    promoted: string[];
    selected: { backendId: string; slot: string } | null;
  };
  search: {
    options: {
      caseSensitive: boolean;
      wholeWord: boolean;
      regex: boolean;
      excludeGitignored: boolean;
      include: string;
      exclude: string;
    };
    recentTerms: string[];
  };
  testProfile: string;
  commandCatalog: {
    commands: CommandInfo[];
    keybindings: ResolvedKeybinding[];
  };
}

export class ClientSession {
  readonly bus: MessageBus;
  readonly state: ClientSessionState;

  constructor(
    readonly connection: HostConnection,
    readonly address: SessionAddress,
  ) {
    this.bus = new MessageBus("session", address, (json) => connection.send(json));
    this.state = new ClientSessionState(this.bus);
  }

  feature(name: string) {
    return this.bus.feature(name);
  }

  get closed(): boolean {
    return this.bus.isClosed;
  }

  get signal(): AbortSignal {
    return this.bus.signal;
  }

  sync(): void {
    void this.feature("lifecycle")
      .request<{ ok: boolean }>("sync", {})
      .catch((error: unknown) => this.connection.reportError(error));
  }

  close(): void {
    this.bus.close("The session is no longer live.");
  }
}

type CatalogListener = (
  catalog: readonly SessionCatalogEntry[],
  sessions: readonly ClientSession[],
) => void;
type HostHelloListener = (hello: HostHello) => void;

export class HostConnection {
  readonly host = new MessageBus("host", null, (json) => this.send(json));
  private readonly sessionsByAddress = new Map<string, ClientSession>();
  private readonly catalogListeners = new Set<CatalogListener>();
  private readonly helloListeners = new Set<HostHelloListener>();
  private catalog: SessionCatalogEntry[] = [];
  private hello: HostHello | null = null;
  private connectTask: Promise<HostHello> | null = null;
  private transportReady = false;
  private readonly earlySessionMessages = new Map<string, MessageEnvelope[]>();
  private readonly sessionCatalogFeature;

  constructor(
    readonly id: string,
    readonly name: string,
    readonly isLocal: boolean,
    private readonly write: (json: string) => void,
    private readonly onError: (error: unknown) => void,
  ) {
    this.sessionCatalogFeature = this.host.feature("sessions");
    this.sessionCatalogFeature.on<SessionCatalogEntry[]>("catalog", (catalog) => {
      this.applyCatalog(catalog);
    });
  }

  get currentHello(): HostHello | null {
    return this.hello;
  }

  get currentCatalog(): readonly SessionCatalogEntry[] {
    return this.catalog;
  }

  get sessions(): readonly ClientSession[] {
    return [...this.sessionsByAddress.values()];
  }

  session(address: SessionAddress): ClientSession | undefined {
    return this.sessionsByAddress.get(this.addressKey(address));
  }

  sessionForSlot(slot: string): ClientSession | undefined {
    const entry = this.catalog.find((candidate) => candidate.id === slot);
    return entry?.address === null || entry?.address === undefined
      ? undefined
      : this.session(entry.address);
  }

  connect(): Promise<HostHello> {
    if (this.transportReady && this.hello !== null) {
      return Promise.resolve(this.hello);
    }
    if (this.connectTask !== null) {
      return this.connectTask;
    }
    const task = this.connectCore();
    this.connectTask = task;
    void task.then(
      () => this.clearConnectTask(task),
      () => this.clearConnectTask(task),
    );
    return task;
  }

  receive(envelope: MessageEnvelope): void {
    if (envelope.scope === "host") {
      this.host.receive(envelope);
      return;
    }
    if (envelope.session === null) {
      return;
    }
    const session = this.session(envelope.session);
    if (!this.transportReady) {
      this.bufferSessionMessage(envelope);
      return;
    }
    if (session === undefined) {
      const address = envelope.session;
      this.sessionCatalogFeature.afterPriorMessages(() => {
        this.session(address)?.bus.receive(envelope);
      });
      return;
    }
    session.bus.receive(envelope);
  }

  disconnect(): void {
    this.host.close("The host connection closed.");
    for (const session of this.sessionsByAddress.values()) {
      session.close();
    }
    this.sessionsByAddress.clear();
    this.catalog = [];
    this.earlySessionMessages.clear();
    this.transportReady = false;
    this.connectTask = null;
    this.hello = null;
    this.publishCatalog();
  }

  transportDropped(): void {
    const reason = "The host connection dropped before the request completed.";
    this.host.linkDropped(reason);
    for (const session of this.sessionsByAddress.values()) {
      session.bus.linkDropped(reason);
    }
    this.transportReady = false;
    this.connectTask = null;
  }

  onCatalog(listener: CatalogListener): () => void {
    this.catalogListeners.add(listener);
    try {
      listener(this.catalog, this.sessions);
    } catch (error) {
      this.reportError(error);
    }
    return () => this.catalogListeners.delete(listener);
  }

  onHello(listener: HostHelloListener): () => void {
    this.helloListeners.add(listener);
    if (this.hello !== null) {
      try {
        listener(this.hello);
      } catch (error) {
        this.reportError(error);
      }
    }
    return () => this.helloListeners.delete(listener);
  }

  send(json: string): void {
    this.write(json);
  }

  reportError(error: unknown): void {
    this.onError(error);
  }

  private clearConnectTask(task: Promise<HostHello>): void {
    if (this.connectTask === task) {
      this.connectTask = null;
    }
  }

  private async connectCore(): Promise<HostHello> {
    const hello = await this.host.feature("connection").request<HostHello>("hello", {});
    this.hello = hello;
    this.applyCatalog(hello.sessions);
    this.transportReady = true;
    this.flushSessionMessages();
    for (const session of this.sessionsByAddress.values()) {
      session.sync();
    }
    for (const listener of this.helloListeners) {
      try {
        listener(hello);
      } catch (error) {
        this.reportError(error);
      }
    }
    return hello;
  }

  private applyCatalog(catalog: SessionCatalogEntry[]): void {
    const live = new Set(
      catalog.flatMap((entry) => (entry.address === null ? [] : [this.addressKey(entry.address)])),
    );
    for (const [key, session] of this.sessionsByAddress) {
      if (!live.has(key)) {
        session.close();
        this.sessionsByAddress.delete(key);
      }
    }
    for (const entry of catalog) {
      if (entry.address === null) {
        continue;
      }
      const key = this.addressKey(entry.address);
      if (!this.sessionsByAddress.has(key)) {
        const session = new ClientSession(this, entry.address);
        this.sessionsByAddress.set(key, session);
      }
    }
    this.catalog = catalog;
    this.publishCatalog();
    if (this.transportReady) {
      this.flushSessionMessages();
    }
  }

  private publishCatalog(): void {
    const sessions = this.sessions;
    for (const listener of this.catalogListeners) {
      try {
        listener(this.catalog, sessions);
      } catch (error) {
        this.reportError(error);
      }
    }
  }

  private bufferSessionMessage(envelope: MessageEnvelope): void {
    if (envelope.session === null) {
      return;
    }
    const key = this.addressKey(envelope.session);
    const pending = this.earlySessionMessages.get(key) ?? [];
    pending.push(envelope);
    this.earlySessionMessages.set(key, pending);
  }

  private flushSessionMessages(): void {
    for (const [key, envelopes] of this.earlySessionMessages) {
      const session = this.sessionsByAddress.get(key);
      if (session === undefined) {
        this.earlySessionMessages.delete(key);
        continue;
      }
      for (const envelope of envelopes) {
        session.bus.receive(envelope);
      }
      this.earlySessionMessages.delete(key);
    }
  }

  private addressKey(address: SessionAddress): string {
    return `${address.slot}\0${address.incarnation}`;
  }
}
