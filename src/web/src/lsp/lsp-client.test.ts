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
  models: [] as FakeModel[],
  onCreate: undefined as ((model: FakeModel) => void) | undefined,
  clients: [] as ClientRecord[],
  channels: [] as ChannelRecord[],
  resets: [] as Array<{ session: FakeSession; name: string; payload: unknown }>,
  notices: [] as Array<{ key: string; text: string }>,
  cleared: [] as string[],
  StartError: class extends Error {},
  startFailure: undefined as Error | undefined,
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
  LspStartError: runtime.StartError,
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
      ready: runtime.startFailure ? Promise.reject(runtime.startFailure) : Promise.resolve(),
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
  notify: (_level: string, text: string, key: string) => runtime.notices.push({ key, text }),
  clearNotification: (key: string) => runtime.cleared.push(key),
}));

function session(id: string, workspace: string): FakeSession {
  const listeners = new Set<(value: WeavieLspConfig | null) => void>();
  const lsp: FakeValue<WeavieLspConfig | null> = {
    current: {
      workspace,
      revision: 0,
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
  runtime.models = [];
  runtime.onCreate = undefined;
  runtime.clients = [];
  runtime.channels = [];
  runtime.resets = [];
  runtime.notices = [];
  runtime.cleared = [];
  runtime.startFailure = undefined;
});

afterEach(() => {
  vi.useRealTimers();
});

describe("session-owned language clients", () => {
  it("starts each identical path on its owning session without consulting selection", async () => {
    const first = session("a", "/repo");
    const second = session("b", "/repo");
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
    session("a", "/repo/a");
    const second = session("b", "/repo/b");
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

  it("installs the same ownership behavior for sessions added after startup", async () => {
    const services = await import("./lsp-client");
    await services.startLanguageServices();
    const later = addSession("later", "/repo/later");

    openModel(model(later, "/repo/later/New.cs"));
    await settle();

    expect(runtime.channels[0]?.owner).toBe(later);
  });

  it("restarts a session's clients when its config changes, and only then", async () => {
    const first = session("a", "/repo");
    const second = session("b", "/repo");
    runtime.models = [model(first, "/repo/Same.cs"), model(second, "/repo/Same.cs")];
    const services = await import("./lsp-client");
    await services.startLanguageServices();
    await settle();
    const config = first.state.lsp.current as WeavieLspConfig;

    first.state.lsp.set({ ...config });
    await settle();
    expect(runtime.channels).toHaveLength(2);

    first.state.lsp.set({ ...config, revision: 1 });
    await settle();

    expect(runtime.channels.map((channel) => [channel.owner.id, channel.disposed])).toEqual([
      ["a", true],
      ["b", false],
      ["a", false],
    ]);
    expect(runtime.clients.map((client) => client.disposed)).toEqual([true, false, false]);
    expect(runtime.notices).toEqual([]);
  });

  it("starts a server whose start failed once the config changes", async () => {
    const owner = session("a", "/repo");
    runtime.models = [model(owner, "/repo/File.cs")];
    runtime.startFailure = new runtime.StartError("C# language server not found (tried /repo/ls)");
    const services = await import("./lsp-client");
    await services.startLanguageServices();
    await settle();

    expect(runtime.clients).toHaveLength(0);
    expect(runtime.notices.map((notice) => notice.text)).toEqual([
      "C# language server not found (tried /repo/ls)",
    ]);
    expect(runtime.cleared).toEqual([]);

    runtime.startFailure = undefined;
    owner.state.lsp.set({ ...(owner.state.lsp.current as WeavieLspConfig), revision: 1 });
    await settle();

    expect(runtime.clients).toHaveLength(1);
    expect(runtime.notices).toHaveLength(1);
    expect(runtime.cleared).toEqual([runtime.notices[0]?.key]);
  });
});
