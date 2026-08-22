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
  owner: FakeSession;
  toString(): string;
}

interface FakeModel {
  uri: FakeUri;
  getLanguageId(): string;
  onDidChangeLanguage(listener: () => void): { dispose(): void };
}

interface ClientRecord {
  disposed: boolean;
  selectors: Array<{ language: string; scheme: string; pattern: string }>;
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

vi.mock("monaco-editor", () => ({
  editor: {
    getModels: () => runtime.models,
    onDidCreateModel: (listener: (model: FakeModel) => void) => {
      runtime.onCreate = listener;
      return { dispose: () => undefined };
    },
  },
  Uri: {
    parse: (value: string) => ({
      toString: () => value,
      hostPath: new URL(value).pathname,
    }),
  },
}));

vi.mock("monaco-languageclient", () => ({
  MonacoLanguageClient: class {
    private readonly record: ClientRecord;
    state = 2;

    constructor(options: {
      clientOptions: {
        documentSelector: ClientRecord["selectors"];
        workspaceFolder: { uri: FakeUri };
        errorHandler: ClientRecord["errorHandler"];
      };
    }) {
      this.record = {
        disposed: false,
        selectors: options.clientOptions.documentSelector,
        workspaceUri: options.clientOptions.workspaceFolder.uri,
        errorHandler: options.clientOptions.errorHandler,
      };
      runtime.clients.push(this.record);
    }

    start(): Promise<void> {
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
      ready: Promise.resolve(),
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
vi.mock("../notify/notify", () => ({ notify: () => undefined }));

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
    const patterns = runtime.clients.map((client) => client.selectors[0]?.pattern);
    expect(new Set(patterns).size).toBe(2);
    expect(patterns).toContain("/sessions/a/repo/**");
    expect(patterns).toContain("/sessions/b/repo/**");
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
