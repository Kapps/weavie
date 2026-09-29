import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WeavieLspConfig } from "./types";

interface FakeValue<T> {
  current: T;
  subscribe(listener: (value: T) => void): () => void;
  set(value: T): void;
}

interface FakeSession {
  id: string;
  address: { slot: string; incarnation: string };
  connection: { id: string };
  state: { lsp: FakeValue<WeavieLspConfig | null> };
  feature(): { publish(name: string, payload: unknown): void };
}

interface FakeUri {
  scheme: string;
  authority: string;
  path: string;
  fragment: string;
  fsPath: string;
  hostPath: string;
  owner?: FakeSession;
  toString(): string;
}

interface FakeModel {
  uri: FakeUri;
  getLanguageId(): string;
  onDidChangeLanguage(listener: () => void): { dispose(): void };
}

interface ClientRecord {
  disposed: boolean;
  selectors: Array<{
    language?: string;
    scheme?: string;
    pattern?: string | { base: string; baseUri: FakeUri; pattern: string };
  }>;
  workspaceUri: FakeUri;
  errorHandler: {
    error(): { action: number; handled: boolean };
    closed(): { action: number; handled: boolean };
  };
}

interface ChannelRecord {
  owner: FakeSession;
  server: string;
  disposed: boolean;
  onExit: (code: number, reason: string | undefined) => void;
}

type Installer = (session: FakeSession) => undefined | (() => void);

const runtime = vi.hoisted(() => ({
  sessions: [] as FakeSession[],
  cleanups: new Map<FakeSession, () => void>(),
  installer: undefined as Installer | undefined,
  selected: null as FakeSession | null,
  models: [] as FakeModel[],
  onCreate: undefined as ((model: FakeModel) => void) | undefined,
  clients: [] as ClientRecord[],
  channels: [] as ChannelRecord[],
  resets: [] as Array<{ session: FakeSession; name: string; payload: unknown }>,
  startError: undefined as Error | undefined,
  notices: [] as Array<{ level: string; message: string; key: string | undefined }>,
}));

function fakeUri(owner: FakeSession, path: string): FakeUri {
  return {
    scheme: "weavie-file",
    authority: `session-${owner.id}`,
    path,
    fragment: owner.id,
    fsPath: `/sessions/${owner.id}${path}`,
    hostPath: path,
    owner,
    toString: () => `file://session-${owner.id}${path}#${owner.id}`,
  };
}

vi.mock("../bridge", () => ({
  log: () => undefined,
  selectedSession: () => runtime.selected,
  registerSessionFeature: (installer: Installer) => {
    runtime.installer = installer;
    for (const session of runtime.sessions) {
      const cleanup = installer(session);
      if (cleanup !== undefined) {
        runtime.cleanups.set(session, cleanup);
      }
    }
    return () => undefined;
  },
}));

vi.mock("../editor/session-uri", () => ({
  SESSION_FILE_SCHEME: "weavie-file",
  sessionForUri: (uri: FakeUri) => uri.owner,
  sessionUriHostPath: (uri: FakeUri) => uri.hostPath,
  sessionFileUri: (session: FakeSession, path: string) => fakeUri(session, path),
  hostUriString: (uri: FakeUri) => `file://${uri.hostPath}`,
  protocolUri: (session: FakeSession, value: string) => fakeUri(session, new URL(value).pathname),
}));

vi.mock("./shared-semantic-tokens-feature", () => ({
  SharedSemanticTokensFeature: class {},
}));

vi.mock("monaco-editor", () => ({
  editor: {
    getModels: () => runtime.models,
    onDidCreateModel: (listener: (model: FakeModel) => void) => {
      runtime.onCreate = listener;
      return { dispose: () => undefined };
    },
  },
  Uri: {
    file: (path: string) => ({
      scheme: "file",
      authority: "",
      path,
      fragment: "",
      fsPath: path,
      hostPath: path,
      toString: () => `file://${path}`,
    }),
    parse: (value: string) => ({
      toString: () => value,
      hostPath: new URL(value).pathname,
    }),
  },
}));

vi.mock("vscode-languageclient/browser.js", () => ({
  BaseLanguageClient: class {
    private readonly record: ClientRecord;
    private readonly rawSelectors: ClientRecord["selectors"];
    state = 2;

    constructor(
      _id: string,
      _name: string,
      clientOptions: {
        documentSelector: ClientRecord["selectors"];
        workspaceFolder: { uri: FakeUri };
        errorHandler: ClientRecord["errorHandler"];
      },
    ) {
      const options = { clientOptions };
      this.rawSelectors = options.clientOptions.documentSelector;
      this.record = {
        disposed: false,
        selectors: options.clientOptions.documentSelector,
        workspaceUri: options.clientOptions.workspaceFolder.uri,
        errorHandler: options.clientOptions.errorHandler,
      };
      runtime.clients.push(this.record);
    }

    get protocol2CodeConverter(): {
      asDocumentSelector(selector: ClientRecord["selectors"]): ClientRecord["selectors"];
    } {
      return { asDocumentSelector: (selector) => selector };
    }

    registerFeature(): void {}

    start(): Promise<void> {
      this.record.selectors = this.protocol2CodeConverter.asDocumentSelector(this.rawSelectors);
      return Promise.resolve();
    }

    dispose(): Promise<void> {
      this.record.disposed = true;
      return Promise.resolve();
    }
  },
}));

vi.mock("./lsp-bridge-transport", () => ({
  LspStartError: class extends Error {},
  openLspChannel: (
    owner: FakeSession,
    server: string,
    _channel: string,
    onExit: (code: number, reason: string | undefined) => void,
  ) => {
    const record = { owner, server, disposed: false, onExit };
    runtime.channels.push(record);
    return {
      reader: {},
      writer: {},
      ready:
        runtime.startError === undefined ? Promise.resolve() : Promise.reject(runtime.startError),
      dispose: () => {
        record.disposed = true;
      },
    };
  },
}));

vi.mock("vscode-languageclient");

vi.mock("../editor/vscode-services", () => ({
  initEditorServices: () => Promise.resolve(),
}));
vi.mock("../notify/notify", () => ({
  notify: (level: string, message: string, key: string | undefined) => {
    runtime.notices.push({ level, message, key });
  },
}));

function session(id: string, workspace: string): FakeSession {
  const listeners = new Set<(value: WeavieLspConfig | null) => void>();
  const lsp: FakeValue<WeavieLspConfig | null> = {
    current: {
      workspace,
      servers: [{ id: "csharp", languageIds: ["csharp"], settings: null }],
    },
    subscribe(listener) {
      listeners.add(listener);
      listener(this.current);
      return () => listeners.delete(listener);
    },
    set(value) {
      this.current = value;
      for (const listener of listeners) {
        listener(value);
      }
    },
  };
  const created: FakeSession = {
    id,
    address: { slot: id, incarnation: `${id}-1` },
    connection: { id: `host-${id}` },
    state: { lsp },
    feature: () => ({
      publish: (name, payload) => runtime.resets.push({ session: created, name, payload }),
    }),
  };
  runtime.sessions.push(created);
  return created;
}

function addSession(id: string, workspace: string): FakeSession {
  const created = session(id, workspace);
  const cleanup = runtime.installer?.(created);
  if (cleanup !== undefined) {
    runtime.cleanups.set(created, cleanup);
  }
  return created;
}

function model(owner: FakeSession, path: string, language = "csharp"): FakeModel {
  return {
    uri: fakeUri(owner, path),
    getLanguageId: () => language,
    onDidChangeLanguage: () => ({ dispose: () => undefined }),
  };
}

function openModel(created: FakeModel): void {
  runtime.models.push(created);
  runtime.onCreate?.(created);
}

async function settle(): Promise<void> {
  await vi.advanceTimersByTimeAsync(0);
}

beforeEach(() => {
  vi.resetModules();
  vi.useFakeTimers();
  runtime.sessions = [];
  runtime.cleanups = new Map();
  runtime.installer = undefined;
  runtime.selected = null;
  runtime.models = [];
  runtime.onCreate = undefined;
  runtime.clients = [];
  runtime.channels = [];
  runtime.resets = [];
  runtime.startError = undefined;
  runtime.notices = [];
});

afterEach(() => {
  vi.useRealTimers();
});

describe("session-owned language clients", () => {
  it("starts each identical path on its owning session without consulting selection", async () => {
    const first = session("a", "/repo");
    const second = session("b", "/repo");
    runtime.selected = second;
    runtime.models = [model(first, "/repo/Same.cs"), model(second, "/repo/Same.cs")];

    const services = await import("./lsp-client");
    await services.startLanguageServices();
    await settle();

    expect(runtime.channels.map((channel) => channel.owner)).toEqual([first, second]);
    expect(runtime.clients).toHaveLength(2);
    const bases = runtime.clients.map((client) => {
      const pattern = client.selectors[0]?.pattern;
      return typeof pattern === "string" ? undefined : pattern?.base;
    });
    expect(new Set(bases).size).toBe(2);
    expect(bases).toContain("/sessions/a/repo");
    expect(bases).toContain("/sessions/b/repo");
    expect(runtime.clients.map((client) => client.workspaceUri.fsPath)).toEqual(["/repo", "/repo"]);
  });

  it("retains config delivered before Monaco starts", async () => {
    const owner = session("early", "/repo/early");
    const services = await import("./lsp-client");
    openModel(model(owner, "/repo/early/File.cs"));

    expect(runtime.clients).toHaveLength(0);
    await services.startLanguageServices();
    await settle();

    expect(runtime.channels[0]?.owner).toBe(owner);
    expect(runtime.clients).toHaveLength(1);
  });

  it("starts a model created later on the model's owner", async () => {
    const first = session("a", "/repo/a");
    const second = session("b", "/repo/b");
    runtime.selected = first;
    const services = await import("./lsp-client");
    await services.startLanguageServices();

    openModel(model(second, "/repo/b/Later.cs"));
    await settle();

    expect(runtime.channels[0]?.owner).toBe(second);
  });

  it("tears down only the session that closes", async () => {
    const first = session("a", "/repo");
    const second = session("b", "/repo");
    runtime.models = [model(first, "/repo/Same.cs"), model(second, "/repo/Same.cs")];
    const services = await import("./lsp-client");
    await services.startLanguageServices();
    await settle();

    runtime.cleanups.get(first)?.();
    await settle();

    expect(runtime.channels.find((channel) => channel.owner === first)?.disposed).toBe(true);
    expect(runtime.channels.find((channel) => channel.owner === second)?.disposed).toBe(false);
    expect(runtime.clients.filter((client) => !client.disposed)).toHaveLength(1);
  });

  it("keeps upstream connection failures out of the toast stack", async () => {
    const owner = session("quiet", "/repo");
    runtime.models = [model(owner, "/repo/File.cs")];
    const services = await import("./lsp-client");
    await services.startLanguageServices();
    await settle();

    const handler = runtime.clients[0]?.errorHandler;
    expect(handler?.error()).toEqual({ action: 1, handled: true });
    expect(handler?.closed()).toEqual({ action: 1, handled: true });
  });

  it("updates one keyed warning when later files retry an unavailable language server", async () => {
    const owner = session("missing", "/repo");
    const { LspStartError } = await import("./lsp-bridge-transport");
    const services = await import("./lsp-client");
    await services.startLanguageServices();

    runtime.startError = new LspStartError("no server installed");
    openModel(model(owner, "/repo/First.cs"));
    await settle();
    runtime.startError = new LspStartError("server still unavailable");
    openModel(model(owner, "/repo/Second.cs"));
    await settle();

    expect(runtime.channels).toHaveLength(2);
    expect(runtime.channels.every((channel) => channel.disposed)).toBe(true);
    expect(runtime.clients).toHaveLength(0);
    expect(runtime.notices).toHaveLength(2);
    const first = runtime.notices[0]!;
    const second = runtime.notices[1]!;
    expect(first.key).toEqual(expect.any(String));
    expect(first.key).not.toBe("");
    expect(second.key).toBe(first.key);
    expect(first.message).toContain("no server installed");
    expect(second.message).toContain("server still unavailable");
    expect(second.level).toBe("warn");
  });

  it("keeps unavailable-server warnings separate by connection, slot, incarnation and server", async () => {
    const first = session("a", "/repo");
    const slot = session("b", "/repo");
    slot.connection.id = first.connection.id;
    slot.address.incarnation = first.address.incarnation;
    const incarnation = session("c", "/repo");
    incarnation.connection.id = first.connection.id;
    incarnation.address.slot = first.address.slot;
    const connection = session("d", "/repo");
    connection.address = { ...first.address };
    first.state.lsp.current!.servers.push({
      id: "typescript",
      languageIds: ["typescript"],
      settings: null,
    });
    runtime.models = [first, slot, incarnation, connection].map((owner) =>
      model(owner, "/repo/File.cs"),
    );
    runtime.models.push(model(first, "/repo/File.ts", "typescript"));
    const { LspStartError } = await import("./lsp-bridge-transport");
    runtime.startError = new LspStartError("no server installed");
    const services = await import("./lsp-client");
    await services.startLanguageServices();
    await settle();

    expect(runtime.notices).toHaveLength(5);
    expect(runtime.notices.every((notice) => typeof notice.key === "string")).toBe(true);
    expect(new Set(runtime.notices.map((notice) => notice.key)).size).toBe(5);
  });

  it("reads the workspace root from the selected session's owned state", async () => {
    const first = session("a", "/repo/a");
    const second = session("b", "/repo/b");
    const services = await import("./lsp-client");

    runtime.selected = first;
    expect(services.currentWorkspaceRoot()).toBe("/repo/a");
    runtime.selected = second;
    expect(services.currentWorkspaceRoot()).toBe("/repo/b");
  });

  it("installs the same ownership behavior for sessions added after startup", async () => {
    const services = await import("./lsp-client");
    await services.startLanguageServices();
    const later = addSession("later", "/repo/later");

    openModel(model(later, "/repo/later/New.cs"));
    await settle();

    expect(runtime.channels[0]?.owner).toBe(later);
  });
});
