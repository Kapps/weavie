import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Drives lsp-client + language-client-pool end to end with monaco, the language client, and the bridge transport
// stubbed. The invariant under test: one live MonacoLanguageClient per (backend, slot, server) — a session switch
// KEEPS the outgoing worktree's client warm (no cold re-index on switch-back), a worktree's providers are scoped
// to its own files (so two same-language sessions never double the "More Actions" menu), and an unload / backend
// switch tears the stranded clients down. Each live client registers one code-action provider, so "live client
// count for a language" is a faithful proxy for "how many providers feed that menu".

interface ClientRec {
  disposed: boolean;
  selectors: Array<{ language: string; scheme?: string; pattern?: string }>;
}
interface FakeModel {
  uri: { scheme: string; authority: string; path: string };
  getLanguageId(): string;
  onDidChangeLanguage(cb: () => void): { dispose(): void };
}
interface ChannelRec {
  slot: string;
  server: string;
  onExit: (code: number, reason?: string) => void;
  disposed: boolean; // dispose() posts lsp-stop, so this doubles as "was lsp-stop sent for this channel".
}

const monacoState = vi.hoisted(() => ({
  models: [] as FakeModel[],
  onCreate: undefined as ((m: FakeModel) => void) | undefined,
}));
const mlc = vi.hoisted(() => ({ instances: [] as ClientRec[] }));
const channels = vi.hoisted(() => ({ list: [] as ChannelRec[] }));
const bus = vi.hoisted(() => ({
  backend: "local",
  sessionHandlers: [] as Array<(msg: unknown, backendId: string) => void>,
}));

vi.mock("monaco-editor", () => ({
  editor: {
    getModels: () => monacoState.models,
    onDidCreateModel: (cb: (m: FakeModel) => void) => {
      monacoState.onCreate = cb;
      return { dispose() {} };
    },
  },
  Uri: { file: (p: string) => ({ scheme: "file", authority: "", path: p, toString: () => p }) },
}));

vi.mock("monaco-languageclient", () => ({
  MonacoLanguageClient: class {
    private readonly rec: ClientRec;
    constructor(opts: { clientOptions: { documentSelector: ClientRec["selectors"] } }) {
      this.rec = { disposed: false, selectors: opts.clientOptions.documentSelector };
      mlc.instances.push(this.rec);
    }
    start(): Promise<void> {
      return Promise.resolve();
    }
    dispose(): Promise<void> {
      this.rec.disposed = true;
      return Promise.resolve();
    }
  },
}));

vi.mock("./lsp-bridge-transport", () => ({
  openLspChannel: (
    slot: string,
    server: string,
    _channelId: string,
    onExit: (code: number, reason?: string) => void,
  ) => {
    const rec: ChannelRec = { slot, server, onExit, disposed: false };
    channels.list.push(rec);
    return {
      reader: {},
      writer: {},
      dispose: () => {
        rec.disposed = true;
      },
    };
  },
}));

vi.mock("vscode-languageclient", () => ({
  CloseAction: { DoNotRestart: 1 },
  ErrorAction: { Continue: 1 },
}));
vi.mock("../bridge", () => ({
  log: () => {},
  postToBackend: () => {},
  activeBackendId: () => bus.backend,
  onSessionMessage: (h: (msg: unknown, backendId: string) => void) => {
    bus.sessionHandlers.push(h);
    return () => {};
  },
}));
vi.mock("../editor/vscode-services", () => ({ initEditorServices: () => Promise.resolve() }));
vi.mock("../notify/notify", () => ({ notify: () => {} }));

const ROOT_A = "/repo/a";
const ROOT_B = "/repo/b";

function csharpConfig(slot: string, workspace: string) {
  return {
    slot,
    workspace,
    servers: [{ id: "csharp", languageIds: ["csharp"], settings: null }],
  };
}

function model(path: string, lang = "csharp", scheme = "file"): FakeModel {
  return {
    uri: { scheme, authority: "", path },
    getLanguageId: () => lang,
    onDidChangeLanguage: () => ({ dispose() {} }),
  };
}

// Open a model: add it to the editor and fire the create hook (as monaco would).
function openModel(m: FakeModel): void {
  monacoState.models.push(m);
  monacoState.onCreate?.(m);
}

function liveClients(): number {
  return mlc.instances.filter((c) => !c.disposed).length;
}

function emitSessionList(
  sessions: Array<{ id: string; loaded: boolean }>,
  backendId: string,
): void {
  for (const h of bus.sessionHandlers) {
    h({ type: "session-list", sessions }, backendId);
  }
}

// Flush pending microtasks (the async dispose chain) without firing the reconnect timer.
async function settle(): Promise<void> {
  await vi.advanceTimersByTimeAsync(0);
}

beforeEach(() => {
  vi.resetModules();
  vi.useFakeTimers();
  monacoState.models = [];
  monacoState.onCreate = undefined;
  mlc.instances = [];
  channels.list = [];
  bus.backend = "local";
  bus.sessionHandlers = [];
  vi.stubGlobal("window", { __WEAVIE_LSP__: csharpConfig("A", ROOT_A) });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("warm language-client pool", () => {
  it("keeps the outgoing worktree's client warm across a session switch", async () => {
    const mod = await import("./lsp-client");
    openModel(model(`${ROOT_A}/Foo.cs`));
    await mod.startLanguageServices();
    await settle();
    expect(liveClients()).toBe(1);
    const channelA = channels.list[0];

    // Switch to B (same backend). B's tabs replace A's in the editor.
    monacoState.models = [model(`${ROOT_B}/Bar.cs`)];
    await mod.rebindLanguageServices(csharpConfig("B", ROOT_B));
    await settle();

    // A's client stays warm — not disposed, no lsp-stop — while B's starts. Two warm clients, one per worktree.
    expect(channelA?.disposed).toBe(false);
    expect(liveClients()).toBe(2);
  });

  it("reuses the warm client on switch-back instead of cold-starting", async () => {
    const mod = await import("./lsp-client");
    openModel(model(`${ROOT_A}/Foo.cs`));
    await mod.startLanguageServices();
    await settle();

    // A → B (A's model removed from the editor) → back to A (its model reopens).
    monacoState.models = [model(`${ROOT_B}/Bar.cs`)];
    await mod.rebindLanguageServices(csharpConfig("B", ROOT_B));
    await settle();
    const constructedAfterSwitch = mlc.instances.length;

    monacoState.models = [model(`${ROOT_A}/Foo.cs`)];
    await mod.rebindLanguageServices(csharpConfig("A", ROOT_A));
    openModel(model(`${ROOT_A}/Foo.cs`));
    await settle();

    // No new client constructed on the return: the warm A client answered.
    expect(mlc.instances.length).toBe(constructedAfterSwitch);
    expect(liveClients()).toBe(2);
  });

  it("scopes each worktree's providers to its own files so two clients never double the menu", async () => {
    const mod = await import("./lsp-client");
    openModel(model(`${ROOT_A}/Foo.cs`));
    await mod.startLanguageServices();
    await settle();

    // Switch to B but keep A's model open too (persistent-model case): both csharp clients live at once.
    await mod.rebindLanguageServices(csharpConfig("B", ROOT_B));
    openModel(model(`${ROOT_B}/Bar.cs`));
    await settle();
    expect(liveClients()).toBe(2);

    const patterns = mlc.instances.map((c) => c.selectors[0]?.pattern);
    expect(patterns).toContain(`${ROOT_A}/**`);
    expect(patterns).toContain(`${ROOT_B}/**`);
    // Disjoint worktree globs: neither client's selector matches the other worktree's files, so only one answers.
    expect(new Set(patterns).size).toBe(2);
    for (const c of mlc.instances) {
      expect(c.selectors[0]?.scheme).toBe("file");
    }
  });

  it("tears the warm client down when its session is unloaded", async () => {
    const mod = await import("./lsp-client");
    openModel(model(`${ROOT_A}/Foo.cs`));
    await mod.startLanguageServices();
    await settle();
    const channelA = channels.list[0];

    // Unload A: session-list arrives with A no longer loaded.
    emitSessionList([{ id: "A", loaded: false }], "local");
    await settle();

    expect(liveClients()).toBe(0);
    expect(channelA?.disposed).toBe(true); // lsp-stop was sent
  });

  it("tears down clients stranded on a different backend after a backend switch", async () => {
    const mod = await import("./lsp-client");
    openModel(model(`${ROOT_A}/Foo.cs`));
    await mod.startLanguageServices();
    await settle();
    const channelA = channels.list[0];

    // The page binds to a remote backend; its session's config arrives on the new backend.
    bus.backend = "remote";
    monacoState.models = [model("/remote/w/Baz.cs")];
    await mod.rebindLanguageServices(csharpConfig("R", "/remote/w"));
    await settle();

    expect(channelA?.disposed).toBe(true); // the local client was stranded and torn down
    expect(liveClients()).toBe(1); // only the remote client remains
  });

  it("does not spawn a duplicate when a stale reconnect fires after the client was replaced", async () => {
    const mod = await import("./lsp-client");
    openModel(model(`${ROOT_A}/Foo.cs`));
    await mod.startLanguageServices();
    await settle();
    expect(liveClients()).toBe(1);

    // A's server exits, scheduling a reconnect timer for A's key.
    channels.list[0]?.onExit(1, "server exited");
    await settle();

    // Before it fires, the same document reopens and re-establishes a fresh live A client (superseding the entry).
    openModel(model(`${ROOT_A}/Foo.cs`));
    await settle();

    // The stale reconnect timer now fires — it must stand down, not resurrect a second client.
    await vi.advanceTimersByTimeAsync(1000);
    expect(liveClients()).toBe(1);
  });

  it("maps a file to the longest-matching worktree root, not a prefix sibling", async () => {
    const mod = await import("./lsp-client");
    // Two sibling worktrees loaded, one a string-prefix of the other. No models open yet.
    await mod.startLanguageServices(); // records A = /repo/a (window config)
    await mod.rebindLanguageServices(csharpConfig("B", "/repo/ab"));
    await settle();

    // A file under /repo/ab must bind to B (/repo/ab), never the prefix sibling A (/repo/a).
    openModel(model("/repo/ab/Bar.cs"));
    await settle();
    expect(mlc.instances.length).toBe(1);
    expect(mlc.instances[0]?.selectors[0]?.pattern).toBe("/repo/ab/**");
  });

  it("does not resurrect a pruned client from a lingering foreign-backend model", async () => {
    const mod = await import("./lsp-client");
    openModel(model(`${ROOT_A}/Foo.cs`));
    await mod.startLanguageServices();
    await settle();
    const channelA = channels.list[0];

    // Switch to a remote backend while A's local model lingers in the editor (models persist across switches).
    bus.backend = "remote";
    monacoState.models = [model(`${ROOT_A}/Foo.cs`), model("/remote/w/Baz.cs")];
    await mod.rebindLanguageServices(csharpConfig("R", "/remote/w"));
    await settle();

    // Local A was pruned and NOT re-created over the wrong bridge by its lingering model; only remote is live.
    expect(channelA?.disposed).toBe(true);
    const livePatterns = mlc.instances
      .filter((c) => !c.disposed)
      .map((c) => c.selectors[0]?.pattern);
    expect(livePatterns).toEqual(["/remote/w/**"]);
  });
});
