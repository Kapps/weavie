import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Each MonacoLanguageClient a live LSP client builds registers one code-action provider; Monaco merges every
// registered provider into the "More Actions" menu. So "the C# actions appear twice" == two live clients. This
// suite drives lsp-client's real start/rebind/reconnect logic (monaco + the language client + bridge stubbed) and
// asserts exactly one live client survives — the regression guard for the duplicate-actions bug.

interface ClientRec {
  started: boolean;
  disposed: boolean;
}
interface FakeModel {
  getLanguageId(): string;
  onDidChangeLanguage(cb: (e: { newLanguage: string }) => void): { dispose(): void };
}
interface ChannelRec {
  server: string;
  onExit: (code: number, reason?: string) => void;
  disposed: boolean;
}

const monacoState = vi.hoisted(() => ({
  models: [] as FakeModel[],
  onCreate: undefined as ((m: FakeModel) => void) | undefined,
}));
const mlc = vi.hoisted(() => ({ instances: [] as ClientRec[] }));
const channels = vi.hoisted(() => ({ list: [] as ChannelRec[] }));

vi.mock("monaco-editor", () => ({
  editor: {
    getModels: () => monacoState.models,
    onDidCreateModel: (cb: (m: FakeModel) => void) => {
      monacoState.onCreate = cb;
      return { dispose() {} };
    },
  },
  Uri: { file: (p: string) => ({ path: p, toString: () => p }) },
}));

vi.mock("monaco-languageclient", () => ({
  MonacoLanguageClient: class {
    private readonly rec: ClientRec;
    constructor() {
      this.rec = { started: false, disposed: false };
      mlc.instances.push(this.rec);
    }
    start(): Promise<void> {
      this.rec.started = true;
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
    _slot: string,
    server: string,
    _channelId: string,
    onExit: (code: number, reason?: string) => void,
  ) => {
    const rec: ChannelRec = { server, onExit, disposed: false };
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
vi.mock("../bridge", () => ({ log: () => {} }));
vi.mock("../editor/vscode-services", () => ({ initEditorServices: () => Promise.resolve() }));
vi.mock("../notify/notify", () => ({ notify: () => {} }));

const CONFIG = {
  slot: "slotA",
  workspace: "/repo",
  servers: [{ id: "csharp", languageIds: ["csharp"], settings: null }],
};

function model(lang: string): FakeModel {
  return { getLanguageId: () => lang, onDidChangeLanguage: () => ({ dispose() {} }) };
}

function liveClients(): number {
  return mlc.instances.filter((c) => c.started && !c.disposed).length;
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
  vi.stubGlobal("window", { __WEAVIE_LSP__: CONFIG });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("lsp-client duplicate-client prevention", () => {
  it("leaves exactly one live client across a plain session rebind", async () => {
    const mod = await import("./lsp-client");
    monacoState.models = [model("csharp")];
    await mod.startLanguageServices();
    await mod.rebindLanguageServices(CONFIG);
    await settle();
    expect(liveClients()).toBe(1);
  });

  it("does not spawn a duplicate client when a stale reconnect fires after a session rebind", async () => {
    const mod = await import("./lsp-client");

    // A .cs document is open; start services → one csharp client at generation 0.
    monacoState.models = [model("csharp")];
    await mod.startLanguageServices();
    await settle();
    expect(liveClients()).toBe(1);

    // The server exits, scheduling a reconnect timer captured at generation 0.
    channels.list[0]?.onExit(1, "server exited");
    await settle();

    // A session switch rebinds before that timer fires: generation → 1, a fresh live client replaces it.
    await mod.rebindLanguageServices(CONFIG);
    await settle();
    expect(liveClients()).toBe(1);

    // The stale (generation-0) reconnect timer now fires — it must not disturb the current generation's state.
    await vi.advanceTimersByTimeAsync(1000);

    // Opening another .cs document must reuse the live client, not start a second one (the duplicate-actions bug).
    const next = model("csharp");
    monacoState.models = [next];
    monacoState.onCreate?.(next);
    await settle();

    expect(liveClients()).toBe(1);
  });
});
